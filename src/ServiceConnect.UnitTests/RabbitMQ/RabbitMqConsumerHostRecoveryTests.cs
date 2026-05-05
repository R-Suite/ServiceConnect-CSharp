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
/// Verifies that <see cref="RabbitMqConsumerHost"/> refreshes its cached consumer tag when
/// the broker assigns a new tag during auto-recovery via
/// <see cref="IConnection.ConsumerTagChangeAfterRecoveryAsync"/>.
///
/// The cached tag must stay current so the <c>BasicCancelAsync</c> call during
/// <c>DisposeAsync</c> targets the live consumer rather than a stale tag.
/// </summary>
public sealed class RabbitMqConsumerHostRecoveryTests
{
    [Fact]
    public async Task ConsumerTag_UpdatedAfterRecovery_WhenTagBefore_MatchesCurrent()
    {
        var (host, fakeConn) = await BuildHostAsync(initialConsumerTag: "tag-original");

        // Verify the initial tag is what BasicConsumeAsync returned.
        var consumerTagField = typeof(RabbitMqConsumerHost).GetField(
            "_consumerTag",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal("tag-original", (string?)consumerTagField.GetValue(host));

        // Simulate broker assigning a new tag after auto-recovery.
        var eventArgs = new ConsumerTagChangedAfterRecoveryEventArgs("tag-original", "tag-recovered", CancellationToken.None);
        await fakeConn.RaiseConsumerTagChangedAsync(eventArgs);

        Assert.Equal("tag-recovered", (string?)consumerTagField.GetValue(host));
    }

    [Fact]
    public async Task ConsumerTag_NotUpdated_WhenTagBefore_DoesNotMatch()
    {
        var (host, fakeConn) = await BuildHostAsync(initialConsumerTag: "tag-original");

        var consumerTagField = typeof(RabbitMqConsumerHost).GetField(
            "_consumerTag",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        // TagBefore refers to a different consumer — should not touch _consumerTag.
        var eventArgs = new ConsumerTagChangedAfterRecoveryEventArgs("tag-other-consumer", "tag-other-recovered", CancellationToken.None);
        await fakeConn.RaiseConsumerTagChangedAsync(eventArgs);

        Assert.Equal("tag-original", (string?)consumerTagField.GetValue(host));
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static async Task<(RabbitMqConsumerHost Host, FakeUnderlyingConnection FakeConn)> BuildHostAsync(
        string initialConsumerTag = "tag-original")
    {
        var fakeConn = new FakeUnderlyingConnection();

        var consumerChannel = new Mock<IChannel>(MockBehavior.Loose);
        consumerChannel.SetupGet(c => c.IsOpen).Returns(true);
        consumerChannel
            .Setup(c => c.BasicQosAsync(
                It.IsAny<uint>(), It.IsAny<ushort>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumerChannel
            .Setup(c => c.BasicConsumeAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<IAsyncBasicConsumer>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(initialConsumerTag);
        consumerChannel
            .SetupAdd(c => c.ChannelShutdownAsync += It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());
        consumerChannel
            .SetupRemove(c => c.ChannelShutdownAsync -= It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());

        var publishChannel = new Mock<IChannel>(MockBehavior.Loose);
        publishChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var conn = new Mock<IServiceConnectConnection>();
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(consumerChannel.Object);
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(publishChannel.Object);
        conn.SetupGet(c => c.UnderlyingConnection).Returns(fakeConn);

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
            conn.Object, transport.Object, queue.Object, bus.Object,
            retry, new RabbitMqAdmissionGate("q"), audit, NullLogger.Instance);

        await host.StartConsumingAsync(
            (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }),
            queueName: "q");

        return (host, fakeConn);
    }

    // ── Test double ───────────────────────────────────────────────────────────

    /// <summary>
    /// Concrete stub that implements only the parts of <see cref="IConnection"/>
    /// needed to raise <see cref="ConsumerTagChangeAfterRecoveryAsync"/> in tests.
    /// All other members throw <see cref="NotImplementedException"/> because
    /// they are irrelevant to the recovery scenario under test.
    /// </summary>
    private sealed class FakeUnderlyingConnection : IConnection
    {
        private AsyncEventHandler<ConsumerTagChangedAfterRecoveryEventArgs>? _consumerTagChanged;

        public event AsyncEventHandler<ConsumerTagChangedAfterRecoveryEventArgs> ConsumerTagChangeAfterRecoveryAsync
        {
            add => _consumerTagChanged += value;
            remove => _consumerTagChanged -= value;
        }

        public Task RaiseConsumerTagChangedAsync(ConsumerTagChangedAfterRecoveryEventArgs args)
        {
            var handler = _consumerTagChanged;
            return handler is not null ? handler(this, args) : Task.CompletedTask;
        }

        // ── Unused IConnection members ────────────────────────────────────────

        public ushort ChannelMax => throw new NotImplementedException();
        public IDictionary<string, object?> ClientProperties => throw new NotImplementedException();
        public TimeSpan Heartbeat => throw new NotImplementedException();
        public bool IsOpen => throw new NotImplementedException();
        public AmqpTcpEndpoint Endpoint => throw new NotImplementedException();
        public IProtocol Protocol => throw new NotImplementedException();
        public uint FrameMax => throw new NotImplementedException();
        public ShutdownEventArgs? CloseReason => throw new NotImplementedException();
        public string ClientProvidedName => throw new NotImplementedException();
        public IDictionary<string, object?>? ServerProperties => throw new NotImplementedException();
        public IEnumerable<ShutdownReportEntry> ShutdownReport => throw new NotImplementedException();
        public int LocalPort => throw new NotImplementedException();
        public int RemotePort => throw new NotImplementedException();

#pragma warning disable CS0067
        public event AsyncEventHandler<CallbackExceptionEventArgs>? CallbackExceptionAsync;
        public event AsyncEventHandler<ConnectionBlockedEventArgs>? ConnectionBlockedAsync;
        public event AsyncEventHandler<ShutdownEventArgs>? ConnectionShutdownAsync;
        public event AsyncEventHandler<AsyncEventArgs>? ConnectionUnblockedAsync;
        public event AsyncEventHandler<AsyncEventArgs>? RecoverySucceededAsync;
        public event AsyncEventHandler<ConnectionRecoveryErrorEventArgs>? ConnectionRecoveryErrorAsync;
        public event AsyncEventHandler<QueueNameChangedAfterRecoveryEventArgs>? QueueNameChangedAfterRecoveryAsync;
        public event AsyncEventHandler<RecoveringConsumerEventArgs>? RecoveringConsumerAsync;
#pragma warning restore CS0067

        public Task<IChannel> CreateChannelAsync(CreateChannelOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task CloseAsync(ushort reasonCode, string reasonText, TimeSpan timeout, bool abort, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task UpdateSecretAsync(string newSecret, string reason, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
