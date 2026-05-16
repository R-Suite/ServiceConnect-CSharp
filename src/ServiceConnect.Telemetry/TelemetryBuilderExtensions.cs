using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect;

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

        // InsertOutermost on both pipelines so the telemetry middleware brackets every
        // other middleware (span starts first, ends last) and so a repeat AddTelemetry
        // call doesn't double-register — each Insert*Outermost method de-duplicates by
        // middleware type so we don't emit two activities per message.
        builder
            .InsertSendMessageMiddlewareOutermost<TelemetrySendMiddleware>()
            .InsertMessageProcessingMiddlewareOutermost<TelemetryProcessingMiddleware>();

        return builder;
    }
}
