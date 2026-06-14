using OpenTelemetry;
using OpenTelemetry.Metrics;
using ServiceConnect.Diagnostics;
using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.UnitTests.Telemetry;

public class TelemetryMeterExtensionsTests
{
    [Fact]
    public void AddServiceConnectInstrumentation_SubscribesToServiceConnectBusMeter()
    {
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddServiceConnectInstrumentation()
            .Build();

        Assert.NotNull(meterProvider);
    }

    [Fact]
    public void AddServiceConnectInstrumentation_ThrowsOnNullBuilder()
    {
        MeterProviderBuilder? builder = null;

        Assert.Throws<ArgumentNullException>(
            () => TelemetryMeterExtensions.AddServiceConnectInstrumentation(builder!));
    }
}
