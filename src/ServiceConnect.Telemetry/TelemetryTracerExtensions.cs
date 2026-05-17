using OpenTelemetry.Trace;

namespace ServiceConnect.Telemetry;

/// <summary>
/// Registers ServiceConnect's <see cref="System.Diagnostics.ActivitySource"/> with
/// an OpenTelemetry tracer provider so publish, send, and consume activities are
/// exported.
/// </summary>
public static class TelemetryTracerExtensions
{
    /// <summary>
    /// Subscribes the OpenTelemetry tracer provider to the activity source emitted
    /// by <see cref="ServiceConnectActivitySource"/>.
    /// </summary>
    /// <param name="builder">The tracer provider builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// Equivalent to calling
    /// <c>builder.AddSource(ServiceConnectActivitySource.ActivitySourceName)</c>;
    /// using this extension keeps the source name in one place, so a rename never
    /// silently disables a caller's telemetry.
    /// </remarks>
    public static TracerProviderBuilder AddServiceConnectInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddSource(ServiceConnectActivitySource.ActivitySourceName);
    }
}
