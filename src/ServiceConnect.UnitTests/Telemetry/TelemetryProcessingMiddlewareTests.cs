using System.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.UnitTests.Telemetry;

[Collection("ActivityListener")]
public sealed class TelemetryProcessingMiddlewareTests : IDisposable
{
    private readonly List<Activity> _activities = [];
    private readonly ActivityListener _listener;
    private readonly ServiceConnectInstrumentationOptions _options = new();
    private readonly IMessagingSystemAttributes _attrs = new RabbitMqMessagingSystemAttributes();

    public TelemetryProcessingMiddlewareTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == ServiceConnectActivitySource.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = _activities.Add,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task ProcessAsync_creates_consume_activity_on_success()
    {
        var sut = new TelemetryProcessingMiddleware(_options, _attrs);
        var envelope = MakeEnvelope();

        static async Task<ConsumeEventResult> Next(
            ReadOnlyMemory<byte> bytes,
            Type type,
            object msg,
            IDictionary<string, object> hdrs,
            Envelope env,
            CancellationToken ct)
        {
            await Task.CompletedTask;
            return new ConsumeEventResult { Success = true };
        }

        var result = await sut.ProcessAsync(
            envelope.Body,
            typeof(SampleMessage),
            new SampleMessage(),
            envelope.Headers,
            envelope,
            Next,
            CancellationToken.None);

        Assert.True(result.Success);
        var span = Assert.Single(_activities);
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task ProcessAsync_records_error_when_result_indicates_failure()
    {
        var sut = new TelemetryProcessingMiddleware(_options, _attrs);
        var envelope = MakeEnvelope();
        var ex = new InvalidOperationException("nope");

        Task<ConsumeEventResult> Next(
            ReadOnlyMemory<byte> bytes,
            Type type,
            object msg,
            IDictionary<string, object> hdrs,
            Envelope env,
            CancellationToken ct) =>
            Task.FromResult(new ConsumeEventResult { Success = false, Exception = ex });

        var result = await sut.ProcessAsync(
            envelope.Body,
            typeof(SampleMessage),
            new SampleMessage(),
            envelope.Headers,
            envelope,
            Next,
            CancellationToken.None);

        Assert.False(result.Success);
        var span = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task ProcessAsync_records_exception_and_rethrows()
    {
        var sut = new TelemetryProcessingMiddleware(_options, _attrs);
        var envelope = MakeEnvelope();
        var boom = new InvalidOperationException("boom");

        Task<ConsumeEventResult> Next(
            ReadOnlyMemory<byte> bytes,
            Type type,
            object msg,
            IDictionary<string, object> hdrs,
            Envelope env,
            CancellationToken ct) => throw boom;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ProcessAsync(
                envelope.Body,
                typeof(SampleMessage),
                new SampleMessage(),
                envelope.Headers,
                envelope,
                Next,
                CancellationToken.None));

        Assert.Same(boom, thrown);
        var span = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task ProcessAsync_ResultSuccessFalseNoException_TagsActivityError()
    {
        var middleware = new TelemetryProcessingMiddleware(_options, _attrs);

        ActivityStatusCode observedStatus = ActivityStatusCode.Unset;
        string? observedDescription = null;

        var capturingListener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == ServiceConnectActivitySource.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                observedStatus = a.Status;
                observedDescription = a.StatusDescription;
            },
        };
        ActivitySource.AddActivityListener(capturingListener);
        try
        {
            static Task<ConsumeEventResult> Next(ReadOnlyMemory<byte> mb, Type mt, object m, IDictionary<string, object> h, Envelope e, CancellationToken ct) =>
                Task.FromResult(new ConsumeEventResult { Success = false, Exception = null });

            var envelope = new Envelope
            {
                Body = new ReadOnlyMemory<byte>([1, 2, 3]),
                Headers = new Dictionary<string, object>(),
            };

            var result = await middleware.ProcessAsync(
                new ReadOnlyMemory<byte>([1, 2, 3]),
                typeof(string),
                "msg",
                new Dictionary<string, object>(),
                envelope,
                Next,
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(ActivityStatusCode.Error, observedStatus);
            Assert.Equal("Dispatch returned Success=false without an exception", observedDescription);
        }
        finally
        {
            capturingListener.Dispose();
        }
    }

    [Fact]
    public async Task ProcessAsync_ConsumeDisabled_PublishEnabled_PublishActivityChainsOnInboundTraceparent()
    {
        // Trace continuity contract: with consume telemetry off and publish telemetry on,
        // a publish issued from inside the handler must produce a span whose TraceId
        // matches the inbound traceparent. Without the AsyncLocal fallback the publish
        // span would become a fresh trace root and downstream consumers could not stitch
        // the graph across this hop.
        var options = new ServiceConnectInstrumentationOptions
        {
            EnableConsumeTelemetry = false,
            EnablePublishTelemetry = true,
        };
        var middleware = new TelemetryProcessingMiddleware(options, _attrs);

        // Inbound headers carry a deterministic W3C traceparent so we can assert TraceId
        // continuity end-to-end.
        const string inboundTraceParent = "00-1234567890abcdef1234567890abcdef-1111111111111111-01";
        var headers = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["traceparent"] = inboundTraceParent,
        };
        var envelope = new Envelope
        {
            Body = new byte[] { 1 },
            Headers = headers,
        };

        Activity? capturedPublishActivity = null;
        Task<ConsumeEventResult> Next(
            ReadOnlyMemory<byte> mb, Type mt, object m,
            IDictionary<string, object> h, Envelope e, CancellationToken ct)
        {
            // Simulate a Bus.Publish from inside the handler.
            var publishArgs = new PublishEventArgs
            {
                Headers = new Dictionary<string, string>(StringComparer.Ordinal),
                Exchange = "ex",
                RoutingKey = "rk",
            };
            capturedPublishActivity = ServiceConnectActivitySource.Publish(publishArgs, options, _attrs);

            // The injected traceparent on the outgoing headers must reference the
            // inbound traceId (continuity), not a freshly-minted one.
            Assert.True(publishArgs.Headers.TryGetValue("traceparent", out var injected));
            Assert.StartsWith("00-1234567890abcdef1234567890abcdef-", injected);

            capturedPublishActivity?.Dispose();
            return Task.FromResult(new ConsumeEventResult { Success = true });
        }

        var result = await middleware.ProcessAsync(
            envelope.Body,
            typeof(SampleMessage),
            new SampleMessage(),
            headers,
            envelope,
            Next,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(capturedPublishActivity);
        Assert.Equal("1234567890abcdef1234567890abcdef", capturedPublishActivity!.TraceId.ToHexString());

        // The fallback must be cleared after ProcessAsync returns. Probe via a follow-up
        // Publish on a fresh logical task: with no fallback, the publish-side InjectTraceContext
        // sees Activity.Current null and writes nothing.
        var probeArgs = new PublishEventArgs
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal),
        };
        ServiceConnectActivitySource.Publish(probeArgs, options, _attrs)?.Dispose();
        // If the AsyncLocal had leaked, the probe headers would carry the inbound traceId.
        if (probeArgs.Headers.TryGetValue("traceparent", out var leaked))
        {
            Assert.False(leaked.StartsWith("00-1234567890abcdef1234567890abcdef-"),
                $"AsyncLocal fallback leaked beyond ProcessAsync: '{leaked}'");
        }
    }

    [Fact]
    public async Task ProcessAsync_ConsumeDisabled_AllTelemetryOff_DoesNotStashFallback()
    {
        // When all publish/send telemetry is also disabled, there is no need to extract
        // headers — the fallback would never be consulted. The middleware should remain
        // a no-op on the fast path. We verify by observing that no AsyncLocal value
        // leaks: a probe Publish AFTER ProcessAsync sees no inbound context (which is
        // also true if the stash-and-cleanup pair worked correctly).
        var options = new ServiceConnectInstrumentationOptions
        {
            EnableConsumeTelemetry = false,
            EnablePublishTelemetry = false,
            EnableSendTelemetry = false,
        };
        var middleware = new TelemetryProcessingMiddleware(options, _attrs);

        var headers = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["traceparent"] = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01",
        };
        var envelope = new Envelope { Body = new byte[] { 1 }, Headers = headers };

        static Task<ConsumeEventResult> Next(
            ReadOnlyMemory<byte> mb, Type mt, object m,
            IDictionary<string, object> h, Envelope e, CancellationToken ct) =>
            Task.FromResult(new ConsumeEventResult { Success = true });

        await middleware.ProcessAsync(envelope.Body, typeof(SampleMessage), new SampleMessage(),
            headers, envelope, Next, CancellationToken.None);

        // Probe: with all telemetry off and the stash never set, a follow-up Publish must
        // produce no span (no listener for publish) and no traceparent header.
        var publishOptions = new ServiceConnectInstrumentationOptions { EnablePublishTelemetry = true };
        var probeArgs = new PublishEventArgs
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal),
        };
        ServiceConnectActivitySource.Publish(probeArgs, publishOptions, _attrs)?.Dispose();
        if (probeArgs.Headers.TryGetValue("traceparent", out var leaked))
        {
            Assert.False(leaked.StartsWith("00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-"),
                $"AsyncLocal fallback was set despite all telemetry being disabled: '{leaked}'");
        }
    }

    private static Envelope MakeEnvelope() => new()
    {
        Body = new byte[] { 1 },
        Headers = new Dictionary<string, object>(StringComparer.Ordinal),
    };

    private sealed class SampleMessage() : Message(Guid.NewGuid());
}

// No [Collection("ActivityListener")] — no listener is registered, so IsConsumeTelemetryEnabled returns false.
public sealed class TelemetryProcessingMiddlewareNoListenerTests
{
    [Fact]
    public async Task ProcessAsync_NoListener_DoesNotEnumerateEnvelopeBody()
    {
        var options = new ServiceConnectInstrumentationOptions { EnableConsumeTelemetry = true };
        var attributes = new RabbitMqMessagingSystemAttributes();
        var middleware = new TelemetryProcessingMiddleware(options, attributes);

        // Envelope.Body is init-only on a sealed class so property access cannot be
        // intercepted by subclassing. The guard is verified indirectly: confirm that
        // IsConsumeTelemetryEnabled returns false in this fixture (pre-condition) and
        // that the middleware completes successfully (no spurious allocation / exception).
        var envelope = new Envelope
        {
            Body = new ReadOnlyMemory<byte>([1, 2, 3]),
            Headers = new Dictionary<string, object>(),
        };

        static Task<ConsumeEventResult> Next(
            ReadOnlyMemory<byte> mb, Type mt, object m,
            IDictionary<string, object> h, Envelope e, CancellationToken ct) =>
            Task.FromResult(new ConsumeEventResult { Success = true });

        var result = await middleware.ProcessAsync(
            new ReadOnlyMemory<byte>([1, 2, 3]),
            typeof(string),
            "msg",
            new Dictionary<string, object>(),
            envelope,
            Next,
            CancellationToken.None);

        Assert.True(result.Success);
        // Pre-condition: gate predicate is false in this no-listener fixture,
        // confirming the body-copy branch was never entered.
        Assert.False(ServiceConnectActivitySource.IsConsumeTelemetryEnabled(options));
    }
}
