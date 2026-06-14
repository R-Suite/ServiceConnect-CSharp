using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies that unexpected faults in the fire-and-forget
/// <c>CancelHelperPublishesAtDeadlineAsync</c> helper are logged at Warning so a
/// stalled deadline helper produces a production-visible signal.
/// </summary>
public sealed class RabbitMqConsumerHostDeadlineHelperTests
{
    [Fact]
    public async Task UnexpectedFaultInDeadlineHelper_LoggedAtWarning()
    {
        // Arrange: a TimeProvider that throws InvalidOperationException on its second
        // GetUtcNow() call. DisposeAsync calls GetUtcNow() first to compute the
        // deadline, then immediately (synchronously, before the first await) enters
        // CancelHelperPublishesAtDeadlineAsync which calls it again — that second call
        // throws and is caught by the outer catch block, which should log at Warning.
        // Subsequent calls return a stable time so DisposeAsync itself can complete.
        var throwingTimeProvider = new ThrowOnSecondCallTimeProvider();
        var fakeLogger = new FakeLogger<DeadlineHelperTag>();

        var (conn, _, _) = BuildConnection();
        var tcfg = MakeTransportCfg();
        var qcfg = MakeQueueCfg();

        var host = new RabbitMqConsumerHost(
            conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", "q", NullLogger.Instance),
            new RabbitMqAdmissionGate("q"),
            new MessageAuditPublisher(qcfg.Object),
            fakeLogger,
            throwingTimeProvider);

        await host.StartConsumingAsync(
            (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        // Act
        await host.DisposeAsync();

        // Assert: the outer catch in CancelHelperPublishesAtDeadlineAsync must have
        // logged the fault at Warning, not Debug.
        var record = fakeLogger.Collector.GetSnapshot()
            .SingleOrDefault(r => r.Message.Contains("CancelHelperPublishesAtDeadlineAsync best-effort recovery faulted"));
        Assert.NotNull(record);
        Assert.Equal(LogLevel.Warning, record.Level);
    }

    // ── Time provider stub ────────────────────────────────────────────────────

    /// <summary>
    /// Returns a fixed far-future time on the first <see cref="GetUtcNow"/> call so
    /// DisposeAsync's deadline is well in the future, then throws
    /// <see cref="InvalidOperationException"/> on the second call (which lands inside
    /// <c>CancelHelperPublishesAtDeadlineAsync</c> before its first await), triggering
    /// the outer catch block. All subsequent calls return the same far-future time so
    /// DisposeAsync can complete normally.
    /// </summary>
    private sealed class ThrowOnSecondCallTimeProvider : TimeProvider
    {
        private int _callCount;
        private static readonly DateTimeOffset _farFuture =
            new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            var count = Interlocked.Increment(ref _callCount);
            if (count == 2)
            {
                throw new InvalidOperationException("synthetic TimeProvider fault for outer-catch test");
            }

            return _farFuture;
        }
    }

    // ── Construction helpers ──────────────────────────────────────────────────

    private static (Mock<IServiceConnectConnection> Connection, Mock<IChannel> ConsumerChannel, Mock<IChannel> PublishChannel) BuildConnection()
    {
        var consumerChannel = new Mock<IChannel>(MockBehavior.Loose);
        consumerChannel.Setup(c => c.IsOpen).Returns(true);
        consumerChannel.Setup(c => c.BasicQosAsync(
                It.IsAny<uint>(), It.IsAny<ushort>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumerChannel.Setup(c => c.BasicConsumeAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<IAsyncBasicConsumer>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("tag");
        consumerChannel.Setup(c => c.BasicCancelAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
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
        conn.SetupGet(c => c.UnderlyingConnection).Returns((global::RabbitMQ.Client.IConnection?)null);

        return (conn, consumerChannel, publishChannel);
    }

    private static Mock<ITransportConfiguration> MakeTransportCfg()
    {
        var cfg = new Mock<ITransportConfiguration>();
        cfg.SetupGet(c => c.MaxRetries).Returns(3);
        cfg.SetupGet(c => c.PrefetchCount).Returns((ushort)10);
        cfg.SetupProperty(c => c.GracefulShutdownTimeoutMilliseconds, 5000);
        cfg.SetupGet(c => c.ClientSettings).Returns(new Dictionary<string, object>());
        return cfg;
    }

    private static Mock<IQueueConfiguration> MakeQueueCfg()
    {
        var cfg = new Mock<IQueueConfiguration>();
        cfg.SetupGet(c => c.QueueName).Returns("q");
        cfg.SetupGet(c => c.ErrorQueueName).Returns("err");
        cfg.SetupGet(c => c.AuditQueueName).Returns("audit");
        cfg.SetupGet(c => c.DisableErrors).Returns(false);
        cfg.SetupGet(c => c.AuditingEnabled).Returns(false);
        return cfg;
    }

    private static Mock<IBusConfiguration> MakeBusCfg()
    {
        var cfg = new Mock<IBusConfiguration>();
        cfg.SetupGet(c => c.IncludeMachineNameInHeaders).Returns(false);
        cfg.SetupGet(c => c.DeadLetterUnhandledMessages).Returns(false);
        return cfg;
    }

    /// <summary>Placeholder type so FakeLogger has a typed category.</summary>
    public sealed class DeadlineHelperTag { }
}
