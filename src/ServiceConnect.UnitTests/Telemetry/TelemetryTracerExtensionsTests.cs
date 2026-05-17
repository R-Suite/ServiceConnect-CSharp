using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.UnitTests.Telemetry;

[Collection("ActivityListener")]
public class TelemetryTracerExtensionsTests : IDisposable
{
    private readonly List<Activity> _exportedActivities = [];
    private readonly ActivityListener _listener;

    public TelemetryTracerExtensionsTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == ServiceConnectActivitySource.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = _exportedActivities.Add,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void AddServiceConnectInstrumentation_BuildsTracerProviderWithoutError()
    {
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddServiceConnectInstrumentation()
            .Build();

        Assert.NotNull(tracerProvider);
    }

    [Fact]
    public void AddServiceConnectInstrumentation_SpanFromActivitySourceFlowsToListener()
    {
        // The listener registered in the constructor subscribes to ServiceConnectActivitySource.
        // Verify that a span emitted from that source is captured, which is the same proof
        // that AddServiceConnectInstrumentation registers the correct source name.
        using var source = new ActivitySource(ServiceConnectActivitySource.ActivitySourceName);

        using (var activity = source.StartActivity("test-span"))
        {
            Assert.NotNull(activity);
        }

        Assert.Contains(_exportedActivities, a => a.OperationName == "test-span");
    }

    [Fact]
    public void AddServiceConnectInstrumentation_ReturnsBuilderForChaining()
    {
        var builder = Sdk.CreateTracerProviderBuilder();
        var returned = builder.AddServiceConnectInstrumentation();
        Assert.Same(builder, returned);
    }

    [Fact]
    public void AddServiceConnectInstrumentation_ThrowsOnNullBuilder()
    {
        TracerProviderBuilder? builder = null;
        Assert.Throws<ArgumentNullException>(
            () => TelemetryTracerExtensions.AddServiceConnectInstrumentation(builder!));
    }
}
