using OpenTelemetry.Metrics;
using ServiceConnect.Diagnostics;

namespace ServiceConnect.Telemetry;

/// <summary>
/// OpenTelemetry registration helpers for ServiceConnect's <see cref="ServiceConnectMeter"/>.
/// </summary>
public static class TelemetryMeterExtensions
{
    /// <summary>
    /// Subscribes the OpenTelemetry MeterProvider to ServiceConnect's <c>"ServiceConnect.Bus"</c> meter.
    /// Equivalent to <c>builder.AddMeter(ServiceConnectMeter.MeterName)</c>.
    /// </summary>
    public static MeterProviderBuilder AddServiceConnectInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMeter(ServiceConnectMeter.MeterName);
    }
}
