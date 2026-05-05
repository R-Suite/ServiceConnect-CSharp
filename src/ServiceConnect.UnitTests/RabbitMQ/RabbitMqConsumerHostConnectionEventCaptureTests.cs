using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies that RabbitMqConsumerHost captures the IConnection reference at subscribe time
/// and uses the same captured reference when unsubscribing in DisposeAsync.
///
/// The bug being tested: previously DisposeAsync re-fetched _connection.UnderlyingConnection,
/// which returns null after the parent Connection's DisposeAsync has run. The result was
/// that the four connection-level event handlers were never unsubscribed, leaking them on
/// the original IConnection until GC reclaimed it.
/// </summary>
public sealed class RabbitMqConsumerHostConnectionEventCaptureTests
{
    [Fact]
    public async Task DisposeAsync_AfterParentConnectionUnderlyingNulled_UnsubscribesAgainstCapturedReference()
    {
        // Track event subscribe/unsubscribe counts on a Mock<IConnection>.
        int shutdownSubs = 0;
        int blockedSubs = 0;
        int unblockedSubs = 0;
        int tagChangeSubs = 0;

        var underlyingConn = new Mock<IConnection>();
        underlyingConn.SetupAdd(c => c.ConnectionShutdownAsync += It.IsAny<AsyncEventHandler<ShutdownEventArgs>>())
            .Callback(() => Interlocked.Increment(ref shutdownSubs));
        underlyingConn.SetupRemove(c => c.ConnectionShutdownAsync -= It.IsAny<AsyncEventHandler<ShutdownEventArgs>>())
            .Callback(() => Interlocked.Decrement(ref shutdownSubs));
        underlyingConn.SetupAdd(c => c.ConnectionBlockedAsync += It.IsAny<AsyncEventHandler<ConnectionBlockedEventArgs>>())
            .Callback(() => Interlocked.Increment(ref blockedSubs));
        underlyingConn.SetupRemove(c => c.ConnectionBlockedAsync -= It.IsAny<AsyncEventHandler<ConnectionBlockedEventArgs>>())
            .Callback(() => Interlocked.Decrement(ref blockedSubs));
        underlyingConn.SetupAdd(c => c.ConnectionUnblockedAsync += It.IsAny<AsyncEventHandler<AsyncEventArgs>>())
            .Callback(() => Interlocked.Increment(ref unblockedSubs));
        underlyingConn.SetupRemove(c => c.ConnectionUnblockedAsync -= It.IsAny<AsyncEventHandler<AsyncEventArgs>>())
            .Callback(() => Interlocked.Decrement(ref unblockedSubs));
        underlyingConn.SetupAdd(c => c.ConsumerTagChangeAfterRecoveryAsync += It.IsAny<AsyncEventHandler<ConsumerTagChangedAfterRecoveryEventArgs>>())
            .Callback(() => Interlocked.Increment(ref tagChangeSubs));
        underlyingConn.SetupRemove(c => c.ConsumerTagChangeAfterRecoveryAsync -= It.IsAny<AsyncEventHandler<ConsumerTagChangedAfterRecoveryEventArgs>>())
            .Callback(() => Interlocked.Decrement(ref tagChangeSubs));

        var serviceConn = new Mock<IServiceConnectConnection>();
        serviceConn.SetupGet(c => c.UnderlyingConnection).Returns(underlyingConn.Object);

        var host = await BuildHostAsync(serviceConn);

        // After StartConsumingAsync all 4 events should have exactly one subscriber each.
        Assert.Equal(1, shutdownSubs);
        Assert.Equal(1, blockedSubs);
        Assert.Equal(1, unblockedSubs);
        Assert.Equal(1, tagChangeSubs);

        // Simulate the parent Connection's DisposeAsync having run first: UnderlyingConnection now null.
        // DisposeAsync must use _subscribedUnderlyingConnection (captured at subscribe time)
        // for unsubscription. Re-fetching UnderlyingConnection here would observe null,
        // skip unsubscribe, and leak the four event handlers.
        serviceConn.SetupGet(c => c.UnderlyingConnection).Returns((IConnection?)null);

        await host.DisposeAsync();

        Assert.Equal(0, shutdownSubs);
        Assert.Equal(0, blockedSubs);
        Assert.Equal(0, unblockedSubs);
        Assert.Equal(0, tagChangeSubs);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a RabbitMqConsumerHost wired to the provided serviceConn mock, calls
    /// StartConsumingAsync to wire up the channels and subscribe the connection-level events,
    /// and returns the ready-to-dispose host.
    /// </summary>
    private static async Task<RabbitMqConsumerHost> BuildHostAsync(Mock<IServiceConnectConnection> serviceConn)
    {
        // ── Consumer channel (BasicQos + BasicConsume + BasicAck/Nack) ──────
        var consumerChannel = new Mock<IChannel>(MockBehavior.Loose);
        consumerChannel.SetupGet(c => c.IsOpen).Returns(true);
        consumerChannel.Setup(c => c.BasicQosAsync(
                It.IsAny<uint>(), It.IsAny<ushort>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumerChannel.Setup(c => c.BasicConsumeAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<IAsyncBasicConsumer>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("tag");
        consumerChannel.Setup(c => c.BasicAckAsync(
                It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        consumerChannel.Setup(c => c.BasicNackAsync(
                It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        consumerChannel.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumerChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        consumerChannel.SetupAdd(c => c.ChannelShutdownAsync += It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());
        consumerChannel.SetupRemove(c => c.ChannelShutdownAsync -= It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());

        // ── Publish channel (loose — only DisposeAsync matters) ─────────────
        var publishChannel = new Mock<IChannel>(MockBehavior.Loose);
        publishChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        serviceConn.Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(consumerChannel.Object);
        serviceConn.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(publishChannel.Object);

        // ── Transport / queue / bus configuration ───────────────────────────
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.MaxRetries).Returns(3);
        transport.SetupGet(t => t.PrefetchCount).Returns((ushort)10);
        transport.SetupProperty(t => t.GracefulShutdownTimeoutMilliseconds, 5000);
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");
        queue.SetupGet(q => q.ErrorQueueName).Returns("err");
        queue.SetupGet(q => q.AuditQueueName).Returns("audit");
        queue.SetupGet(q => q.AuditRoutingKey).Returns(string.Empty);
        queue.SetupGet(q => q.DisableErrors).Returns(false);
        queue.SetupGet(q => q.AuditingEnabled).Returns(false);

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);
        bus.SetupGet(b => b.DeadLetterUnhandledMessages).Returns(false);

        var retry = new MessageRetryHandler(3, "err", "q", NullLogger.Instance);
        var audit = new MessageAuditPublisher(queue.Object);

        var host = new RabbitMqConsumerHost(
            serviceConn.Object, transport.Object, queue.Object, bus.Object,
            retry, new RabbitMqAdmissionGate("q"), audit, NullLogger.Instance);

        await host.StartConsumingAsync(
            (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }),
            queueName: "q");

        return host;
    }
}
