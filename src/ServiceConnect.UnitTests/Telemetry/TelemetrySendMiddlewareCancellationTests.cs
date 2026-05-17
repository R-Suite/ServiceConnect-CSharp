using System.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.UnitTests.Telemetry;

[Collection("ActivityListener")]
public sealed class TelemetrySendMiddlewareCancellationTests : IDisposable
{
    private readonly List<Activity> _activities = [];
    private readonly ActivityListener _listener;
    private readonly ServiceConnectInstrumentationOptions _options = new();
    private readonly IMessagingSystemAttributes _attrs = new RabbitMqMessagingSystemAttributes();

    public TelemetrySendMiddlewareCancellationTests()
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
        var sut = new TelemetrySendMiddleware(_options, _attrs);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Task Next(SendContext ctx, CancellationToken ct) => throw new OperationCanceledException(cts.Token);

        var context = new SendContext
        {
            Message = new SampleMessage(),
            MessageType = typeof(SampleMessage),
            MessageBytes = ReadOnlyMemory<byte>.Empty,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal),
            Operation = SendOperation.Publish,
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.ProcessAsync(context, Next, cts.Token));

        var span = Assert.Single(_activities);
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task ProcessAsync_when_next_throws_non_cancellation_exception_span_status_is_error()
    {
        var sut = new TelemetrySendMiddleware(_options, _attrs);

        static Task Next(SendContext ctx, CancellationToken ct) => throw new InvalidOperationException("boom");

        var context = new SendContext
        {
            Message = new SampleMessage(),
            MessageType = typeof(SampleMessage),
            MessageBytes = ReadOnlyMemory<byte>.Empty,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal),
            Operation = SendOperation.Publish,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ProcessAsync(context, Next, CancellationToken.None));

        var span = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    private sealed class SampleMessage() : Message(Guid.NewGuid());
}
