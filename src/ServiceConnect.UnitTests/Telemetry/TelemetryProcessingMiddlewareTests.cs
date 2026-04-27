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

    public TelemetryProcessingMiddlewareTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == ServiceConnectActivitySource.ConsumeActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = _activities.Add,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task ProcessAsync_creates_consume_activity_on_success()
    {
        var sut = new TelemetryProcessingMiddleware();
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
        var sut = new TelemetryProcessingMiddleware();
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
        var sut = new TelemetryProcessingMiddleware();
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

    private static Envelope MakeEnvelope() => new()
    {
        Body = new byte[] { 1 },
        Headers = new Dictionary<string, object>(StringComparer.Ordinal),
    };

    private sealed class SampleMessage() : Message(Guid.NewGuid());
}
