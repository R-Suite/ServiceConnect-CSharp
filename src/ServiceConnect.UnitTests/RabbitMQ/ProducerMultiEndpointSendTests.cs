using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class ProducerMultiEndpointSendTests
{
    [Fact]
    public async Task SendAsync_FanOutToThreeEndpoints_EachDeliveryHasDistinctMessageIdAndTimeSent_SharedCorrelationId()
    {
        var fakeClock = new FakeTimeProvider(new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero));

        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("source-q");
        IReadOnlyList<string> endpoints = ["ep-a", "ep-b", "ep-c"];
        queueConfig.Setup(q => q.TryGetQueueMapping(typeof(FakeMsg), out endpoints!)).Returns(true);

        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        // Capture per-call BasicProperties. BuildBasicProperties creates a new instance with
        // a fresh headers-copy per call, so the references are already independent. We snapshot
        // a defensive copy anyway so the test is robust to any future batching optimisations.
        var captured = new List<BasicProperties>();
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns((string ex, string rk, bool m, BasicProperties bp, ReadOnlyMemory<byte> body, CancellationToken ct) =>
            {
                // Snapshot: copy MessageId and headers into a new BasicProperties so later
                // iterations' mutations to baseHeaders do not retroactively alter earlier captures.
                captured.Add(new BasicProperties
                {
                    MessageId = bp.MessageId,
                    Headers = bp.Headers is null ? null : new Dictionary<string, object?>(bp.Headers, StringComparer.Ordinal),
                    Persistent = bp.Persistent,
                });
                // Advance the clock so successive iterations stamp a later TimeSent.
                fakeClock.Advance(TimeSpan.FromMilliseconds(1));
                return ValueTask.CompletedTask;
            });
        channel
            .Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>>(), false, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var producer = new Producer(transport.Object, queueConfig.Object, busConfig.Object, NullLogger<Producer>.Instance, fakeClock);

        // Inject the fake connection via the test seam so EnsureConnectedAsync routes through
        // our mock channel without touching a real RabbitMQ broker.
        var fakeConnection = new Mock<IConnection>();
        fakeConnection.SetupGet(c => c.IsOpen).Returns(true);
        fakeConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel.Object);
        producer.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(fakeConnection.Object);

        // Bus-stamped CorrelationId — must remain constant across the fan-out.
        var correlationId = Guid.NewGuid().ToString();
        var headers = new Dictionary<string, string>
        {
            [HeaderKeys.CorrelationId] = correlationId,
        };

        await producer.SendAsync(typeof(FakeMsg), new byte[] { 1, 2, 3 }, headers);

        Assert.Equal(3, captured.Count);

        // Invariant: each delivery carries a distinct on-wire identity.
        Assert.Equal(3, captured.Select(c => c.MessageId).Distinct(StringComparer.Ordinal).Count());

        // TimeSent is re-stamped per iteration; the clock advances inside each publish callback,
        // so the three snapshots must all differ.
        var timeSentValues = captured
            .Select(c => c.Headers![HeaderKeys.TimeSent]!.ToString()!)
            .ToList();
        Assert.Equal(3, timeSentValues.Distinct(StringComparer.Ordinal).Count());

        // CorrelationId stays constant — proves we did not accidentally re-mint that.
        var correlationIds = captured
            .Select(c => c.Headers![HeaderKeys.CorrelationId]!.ToString()!)
            .ToList();
        Assert.All(correlationIds, id => Assert.Equal(correlationId, id));
    }

    private sealed class FakeMsg { }
}
