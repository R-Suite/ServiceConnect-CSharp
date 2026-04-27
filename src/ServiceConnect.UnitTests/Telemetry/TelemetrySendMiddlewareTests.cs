using System.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.UnitTests.Telemetry;

[Collection("ActivityListener")]
public sealed class TelemetrySendMiddlewareTests : IDisposable
{
    private readonly List<Activity> _activities = [];
    private readonly ActivityListener _listener;

    public TelemetrySendMiddlewareTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src =>
                src.Name == ServiceConnectActivitySource.PublishActivitySourceName
                || src.Name == ServiceConnectActivitySource.SendActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = _activities.Add,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task ProcessAsync_publish_creates_publish_activity_and_invokes_next()
    {
        var sut = new TelemetrySendMiddleware(new ServiceConnectInstrumentationOptions());
        var nextCalled = false;

        async Task Next(SendContext ctx, CancellationToken ct)
        {
            nextCalled = true;
            await Task.CompletedTask;
        }

        var context = new SendContext
        {
            Message = new SampleMessage(),
            MessageType = typeof(SampleMessage),
            MessageBytes = [1],
            Headers = new Dictionary<string, string>(StringComparer.Ordinal),
            RoutingKey = "rk",
            Operation = SendOperation.Publish,
        };

        await sut.ProcessAsync(context, Next, CancellationToken.None);

        Assert.True(nextCalled);
        var span = Assert.Single(_activities);
        Assert.Equal(ServiceConnectActivitySource.PublishActivitySourceName, span.Source.Name);
    }

    [Fact]
    public async Task ProcessAsync_send_creates_send_activity()
    {
        var sut = new TelemetrySendMiddleware(new ServiceConnectInstrumentationOptions());

        static Task Next(SendContext ctx, CancellationToken ct) => Task.CompletedTask;

        var context = new SendContext
        {
            Message = new SampleMessage(),
            MessageType = typeof(SampleMessage),
            MessageBytes = [],
            Headers = new Dictionary<string, string>(StringComparer.Ordinal),
            EndPoint = "queue.target",
            Operation = SendOperation.Send,
        };

        await sut.ProcessAsync(context, Next, CancellationToken.None);

        var span = Assert.Single(_activities);
        Assert.Equal(ServiceConnectActivitySource.SendActivitySourceName, span.Source.Name);
    }

    [Fact]
    public async Task ProcessAsync_records_exception_and_rethrows()
    {
        var sut = new TelemetrySendMiddleware(new ServiceConnectInstrumentationOptions());
        var boom = new InvalidOperationException("boom");

        Task Next(SendContext ctx, CancellationToken ct) => throw boom;

        var context = new SendContext
        {
            Message = new SampleMessage(),
            MessageType = typeof(SampleMessage),
            MessageBytes = [],
            Headers = new Dictionary<string, string>(StringComparer.Ordinal),
            Operation = SendOperation.Publish,
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ProcessAsync(context, Next, CancellationToken.None));
        Assert.Same(boom, thrown);

        var span = Assert.Single(_activities);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    private sealed class SampleMessage() : Message(Guid.NewGuid());
}
