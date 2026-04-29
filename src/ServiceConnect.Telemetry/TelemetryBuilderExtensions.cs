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
            services.AddSingleton(options);
            services.TryAddSingleton<IMessagingSystemAttributes, RabbitMqMessagingSystemAttributes>();
            services.AddSingleton<TelemetrySendMiddleware>();
            services.AddSingleton<TelemetryProcessingMiddleware>();
        });

        builder.ConfigurePipeline(p =>
        {
            p.SendMessageMiddleware.Insert(0, typeof(TelemetrySendMiddleware));
            p.MessageProcessingMiddleware.Insert(0, typeof(TelemetryProcessingMiddleware));
        });

        return builder;
    }
}
