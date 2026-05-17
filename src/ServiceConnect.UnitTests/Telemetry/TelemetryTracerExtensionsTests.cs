using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.UnitTests.Telemetry;

[Collection("ActivityListener")]
public sealed class TelemetryTracerExtensionsTests
{
    [Fact]
    public void AddServiceConnectInstrumentation_subscribes_provider_to_ServiceConnect_activity_source()
    {
        // Pin: invoking the extension on a TracerProvider must wire it to the
        // ServiceConnect ActivitySource. A regression that called AddSource with
        // the wrong name would leave exportedActivities empty.
        var exportedActivities = new List<Activity>();
        using var tp = Sdk.CreateTracerProviderBuilder()
            .AddServiceConnectInstrumentation()
            .AddInMemoryExporter(exportedActivities)
            .Build();

        using var source = new ActivitySource(ServiceConnectActivitySource.ActivitySourceName);
        using (source.StartActivity("probe")) { }

        // ForceFlush drains any batched spans before the assertion.
        tp.ForceFlush();

        Assert.Contains(exportedActivities, a =>
            a.OperationName == "probe" &&
            a.Source.Name == ServiceConnectActivitySource.ActivitySourceName);
    }

    [Fact]
    public void AddServiceConnectInstrumentation_returns_same_builder_for_chaining()
    {
        var builder = Sdk.CreateTracerProviderBuilder();
        var returned = builder.AddServiceConnectInstrumentation();
        Assert.Same(builder, returned);
    }

    [Fact]
    public void AddServiceConnectInstrumentation_null_builder_throws()
    {
        TracerProviderBuilder? builder = null;
        Assert.Throws<ArgumentNullException>(() => builder!.AddServiceConnectInstrumentation());
    }
}
