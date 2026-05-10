using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect;
using ServiceConnect.Configuration;

namespace ServiceConnect.Telemetry;

/// <summary>
/// Wires the built-in <see cref="TelemetrySendMiddleware"/> and
/// <see cref="TelemetryProcessingMiddleware"/> into a <see cref="ServiceConnectBuilder"/>.
/// </summary>
public static class TelemetryBuilderExtensions
{
    /// <summary>
    /// Registers the built-in telemetry middleware as the outermost middleware
    /// on both the send and processing pipelines, and registers
    /// <see cref="ServiceConnectInstrumentationOptions"/> +
    /// <see cref="IMessagingSystemAttributes"/> in DI. Users can override the
    /// messaging-system attributes by registering <c>IMessagingSystemAttributes</c>
    /// before calling this method.
    /// </summary>
    /// <param name="builder">The builder to configure.</param>
    /// <param name="configure">Optional callback that mutates the instrumentation options.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static ServiceConnectBuilder AddTelemetry(
        this ServiceConnectBuilder builder,
        Action<ServiceConnectInstrumentationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new ServiceConnectInstrumentationOptions();
        configure?.Invoke(options);

        builder.AddRegistration(services =>
        {
            // TryAddSingleton across the board so a second AddTelemetry call (or two
            // feature modules each calling it) does not double-register the middleware
            // or the options. The options instance from the first call wins; that
            // matches the "first registration wins" semantics of TryAdd.
            services.TryAddSingleton(options);
            services.TryAddSingleton<IMessagingSystemAttributes, RabbitMqMessagingSystemAttributes>();
            services.TryAddSingleton<TelemetrySendMiddleware>();
            services.TryAddSingleton<TelemetryProcessingMiddleware>();
        });

        builder.ConfigurePipeline(p =>
        {
            // Guard against duplicate Insert on a second AddTelemetry call. Without the
            // guard each pipeline gets a duplicate entry and emits two activities per
            // message (corrupting OTel cardinality and double-counting publish/consume
            // duration histograms).
            if (!p.SendMessageMiddleware.Contains(typeof(TelemetrySendMiddleware)))
            {
                p.SendMessageMiddleware.Insert(0, typeof(TelemetrySendMiddleware));
            }
            if (!p.MessageProcessingMiddleware.Contains(typeof(TelemetryProcessingMiddleware)))
            {
                p.MessageProcessingMiddleware.Insert(0, typeof(TelemetryProcessingMiddleware));
            }
        });

        return builder;
    }
}
