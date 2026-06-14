using System.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.UnitTests.Telemetry;

[Collection("ActivityListener")]
public sealed class TelemetryProcessingMiddlewareCancellationTests : IDisposable
{
    private readonly List<Activity> _activities = [];
    private readonly ActivityListener _listener;
    private readonly ServiceConnectInstrumentationOptions _options = new();
    private readonly IMessagingSystemAttributes _attrs = new RabbitMqMessagingSystemAttributes();

    public TelemetryProcessingMiddlewareCancellationTests()
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
    public async Task ProcessAsync_when_next_throws_OperationCanceled_span_status_is_not_error()
    {
        var sut = new TelemetryProcessingMiddleware(_options, _attrs);
        var envelope = MakeEnvelope();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Task<ConsumeEventResult> Next(
            ReadOnlyMemory<byte> bytes,
            Type type,
            object msg,
            IDictionary<string, object> hdrs,
            Envelope env,
            CancellationToken ct) => throw new OperationCanceledException(cts.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.ProcessAsync(
                envelope.Body,
                typeof(SampleMessage),
                new SampleMessage(),
                envelope.Headers,
                envelope,
                Next,
                cts.Token));

        var span = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
    }

    [Fact]
    public async Task ProcessAsync_when_next_throws_non_cancellation_exception_span_status_is_error()
    {
        var sut = new TelemetryProcessingMiddleware(_options, _attrs);
        var envelope = MakeEnvelope();

        static Task<ConsumeEventResult> Next(
            ReadOnlyMemory<byte> bytes,
            Type type,
            object msg,
            IDictionary<string, object> hdrs,
            Envelope env,
            CancellationToken ct) => throw new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ProcessAsync(
                envelope.Body,
                typeof(SampleMessage),
                new SampleMessage(),
                envelope.Headers,
                envelope,
                Next,
                CancellationToken.None));

        var span = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task ProcessAsync_when_next_returns_unsuccessful_result_with_OperationCanceled_exception_span_status_is_not_error()
    {
        var sut = new TelemetryProcessingMiddleware(_options, _attrs);
        var envelope = MakeEnvelope();

        static Task<ConsumeEventResult> Next(
            ReadOnlyMemory<byte> bytes,
            Type type,
            object msg,
            IDictionary<string, object> hdrs,
            Envelope env,
            CancellationToken ct) =>
            Task.FromResult(new ConsumeEventResult { Success = false, Exception = new OperationCanceledException() });

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
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
    }

    private static Envelope MakeEnvelope() => new()
    {
        Body = new byte[] { 1 },
        Headers = new Dictionary<string, object>(StringComparer.Ordinal),
    };

    private sealed class SampleMessage() : Message(Guid.NewGuid());
}
