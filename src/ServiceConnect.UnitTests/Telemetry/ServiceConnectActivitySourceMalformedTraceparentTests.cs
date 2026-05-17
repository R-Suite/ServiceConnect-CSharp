using System.Diagnostics;
using System.Text;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.UnitTests.Telemetry;

[Collection("ActivityListener")]
public sealed class ServiceConnectActivitySourceMalformedTraceparentTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ServiceConnectInstrumentationOptions _options = new();
    private readonly IMessagingSystemAttributes _attrs = new StubAttributes();

    public ServiceConnectActivitySourceMalformedTraceparentTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == ServiceConnectActivitySource.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    [Fact]
    public void Consume_WithMalformedTraceparent_ForcesFreshTraceRoot_AndStampsDiagnosticTag()
    {
        // Simulate a hosted environment that has an unrelated ambient activity wrapping the
        // consume loop (e.g. an ASP.NET request span, a host worker activity). A poisoned
        // producer that injects a malformed traceparent must NOT cause the consume span to
        // be parented onto this ambient host activity — that would produce a stitched-but-
        // wrong trace graph pointing at the wrong producer.
        using var ambientSource = new ActivitySource("Test.Ambient.Source");
        using var ambientListener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "Test.Ambient.Source",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(ambientListener);

        using var ambient = ambientSource.StartActivity("ambient", ActivityKind.Internal);
        Assert.NotNull(ambient);
        Assert.NotNull(Activity.Current);
        var ambientTraceId = Activity.Current!.TraceId;

        var args = new ConsumeEventArgs
        {
            Headers = new Dictionary<string, object>
            {
                // Malformed: not a W3C traceparent. The header is present, but
                // ActivityContext.TryParse will return false.
                ["traceparent"] = Encoding.UTF8.GetBytes("NOT-VALID"),
            }
        };

        using var activity = ServiceConnectActivitySource.Consume(args, _options, _attrs);

        Assert.NotNull(activity);
        // Fresh root — the consume span must not inherit the ambient host activity's trace.
        Assert.NotEqual(ambientTraceId, activity!.TraceId);
        // Diagnostic tag so operators can search/filter for poisoned producers.
        Assert.Equal(true, activity.GetTagItem("enrichment.malformed_traceparent"));
    }

    [Fact]
    public void Publish_AfterMalformedTraceparentConsume_InheritsFreshRoot_NotAmbient()
    {
        // After Consume() forces a fresh trace root because of a malformed inbound
        // traceparent, Activity.Current must be the fresh consume span (not the host
        // ambient) for the rest of the consume-side flow. The middleware then runs the
        // user's handler under this ambient, and any Bus.Publish / Bus.Send issued from
        // the handler reads Activity.Current to stamp the outbound traceparent. If the
        // ambient leaks back here, the outbound trace inherits the host ambient — the
        // exact stitching the fresh-root forcing was meant to prevent.
        using var ambientSource = new ActivitySource("Test.Ambient.Source");
        using var ambientListener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "Test.Ambient.Source",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(ambientListener);

        using var ambient = ambientSource.StartActivity("ambient", ActivityKind.Internal);
        Assert.NotNull(ambient);
        Assert.Equal(ambient, Activity.Current);
        var ambientTraceId = ambient!.TraceId;

        var consumeArgs = new ConsumeEventArgs
        {
            Headers = new Dictionary<string, object>
            {
                ["traceparent"] = Encoding.UTF8.GetBytes("NOT-VALID"),
            }
        };

        using var consumeSpan = ServiceConnectActivitySource.Consume(consumeArgs, _options, _attrs);
        Assert.NotNull(consumeSpan);
        // The consume span is a fresh root — its TraceId differs from the ambient's.
        Assert.NotEqual(ambientTraceId, consumeSpan!.TraceId);
        // CRITICAL: Activity.Current must now be the fresh consume span, not the ambient.
        // The middleware will run the user's handler under this ambient and a Publish
        // from inside the handler must inherit this trace, not the host ambient.
        Assert.Equal(consumeSpan, Activity.Current);

        // Simulate a Publish issued from inside the handler. With Activity.Current set to
        // the fresh consume root, the publish span must be parented onto the consume span
        // and share its TraceId — not the ambient host's.
        var publishArgs = new PublishEventArgs
        {
            Exchange = "downstream",
            Message = new Message(Guid.NewGuid()),
        };
        using var publishSpan = ServiceConnectActivitySource.Publish(publishArgs, _options, _attrs);

        Assert.NotNull(publishSpan);
        Assert.Equal(consumeSpan.TraceId, publishSpan!.TraceId);
        Assert.NotEqual(ambientTraceId, publishSpan.TraceId);
    }

    [Fact]
    public void Consume_WithNoTraceparent_DoesNotStampMalformedTag_AndInheritsAmbient()
    {
        // Sanity check the unchanged path: a missing traceparent header should still
        // fall through to Activity.Current (the existing ambient-link behaviour) and
        // must NOT be flagged as malformed.
        using var ambientSource = new ActivitySource("Test.Ambient.Source");
        using var ambientListener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "Test.Ambient.Source",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(ambientListener);

        using var ambient = ambientSource.StartActivity("ambient", ActivityKind.Internal);
        Assert.NotNull(ambient);
        var ambientTraceId = Activity.Current!.TraceId;

        var args = new ConsumeEventArgs
        {
            // No traceparent header at all.
            Headers = new Dictionary<string, object>(),
        };

        using var activity = ServiceConnectActivitySource.Consume(args, _options, _attrs);

        Assert.NotNull(activity);
        // Inherits ambient because no inbound traceparent steered it elsewhere.
        Assert.Equal(ambientTraceId, activity!.TraceId);
        Assert.Null(activity.GetTagItem("enrichment.malformed_traceparent"));
    }

    private sealed class StubAttributes : IMessagingSystemAttributes
    {
        public string MessagingSystem => "test";
        public string ProtocolName => "test";
    }
}
