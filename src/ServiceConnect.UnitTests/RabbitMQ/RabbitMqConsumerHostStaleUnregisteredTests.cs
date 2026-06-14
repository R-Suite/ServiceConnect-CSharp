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
/// Pins the stale-tag gate inside <see cref="RabbitMqConsumerHost.HandleConsumerUnregistered"/>.
/// Under multi-kill chaos, RabbitMQ.Client's async event dispatch can deliver a prior
/// cycle's UnregisteredAsync AFTER the current cycle's RecoverySucceededAsync has cleared
/// the broker-cancelled flag. The gate ignores events whose tag has been superseded by
/// topology recovery's re-issued BasicConsumeAsync.
/// </summary>
public sealed class RabbitMqConsumerHostStaleUnregisteredTests
{
    [Fact]
    public async Task HandleConsumerUnregistered_with_live_tag_marks_cancelled()
    {
        var host = await BuildHostWithLiveTagAsync("live");

        host.HandleConsumerUnregistered(new ConsumerEventArgs(["live"], CancellationToken.None));

        Assert.True(host.IsCancelledByBroker);
    }

    [Fact]
    public async Task HandleConsumerUnregistered_with_stale_tag_does_not_mark_cancelled()
    {
        var host = await BuildHostWithLiveTagAsync("live");

        host.HandleConsumerUnregistered(new ConsumerEventArgs(["stale-from-prior-kill"], CancellationToken.None));

        Assert.False(host.IsCancelledByBroker);
    }

    [Fact]
    public async Task HandleConsumerUnregistered_with_multiple_tags_including_live_marks_cancelled()
    {
        var host = await BuildHostWithLiveTagAsync("live");

        host.HandleConsumerUnregistered(new ConsumerEventArgs(["stale", "live", "another-stale"], CancellationToken.None));

        Assert.True(host.IsCancelledByBroker);
    }

    [Fact]
    public async Task HandleConsumerUnregistered_with_empty_tags_marks_cancelled_fallback()
    {
        var host = await BuildHostWithLiveTagAsync("live");

        host.HandleConsumerUnregistered(new ConsumerEventArgs([], CancellationToken.None));

        Assert.True(host.IsCancelledByBroker);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static async Task<RabbitMqConsumerHost> BuildHostWithLiveTagAsync(string liveTag)
    {
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
            .ReturnsAsync(liveTag);
        consumerChannel.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumerChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        consumerChannel.SetupAdd(c => c.ChannelShutdownAsync += It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());
        consumerChannel.SetupRemove(c => c.ChannelShutdownAsync -= It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());

        var publishChannel = new Mock<IChannel>(MockBehavior.Loose);
        publishChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var conn = new Mock<IServiceConnectConnection>();
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(consumerChannel.Object);
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(publishChannel.Object);
        conn.SetupGet(c => c.UnderlyingConnection).Returns((IConnection?)null);

        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.MaxRetries).Returns(3);
        transport.SetupGet(t => t.PrefetchCount).Returns((ushort)10);
        transport.SetupProperty(t => t.GracefulShutdownTimeoutMilliseconds, 5000);
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");
        queue.SetupGet(q => q.ErrorQueueName).Returns("err");
        queue.SetupGet(q => q.AuditQueueName).Returns("audit");
        queue.SetupGet(q => q.DisableErrors).Returns(false);
        queue.SetupGet(q => q.AuditingEnabled).Returns(false);

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);
        bus.SetupGet(b => b.DeadLetterUnhandledMessages).Returns(false);

        var retry = new MessageRetryHandler(3, "err", "q", NullLogger.Instance);
        var audit = new MessageAuditPublisher(queue.Object);

        var host = new RabbitMqConsumerHost(
            conn.Object, transport.Object, queue.Object, bus.Object,
            retry, new RabbitMqAdmissionGate("q"), audit, NullLogger.Instance);

        await host.StartConsumingAsync(
            (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }),
            queueName: "q");

        return host;
    }
}
