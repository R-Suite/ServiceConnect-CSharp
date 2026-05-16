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
/// Verifies the host's single-use contract: <c>PrepareAsync</c> may be called at most once
/// per instance. A second call throws <see cref="InvalidOperationException"/> rather than
/// silently latching the admission gate into a permanent reject-everything state (the prior
/// implementation reset <c>_disposeStarted</c> and rotated CTSes but left
/// <c>_admissionGate.IsShuttingDown</c>, <c>_stopStarted</c>, and
/// <c>_consumerCancelledByBroker</c> latched — so the second cycle's deliveries were dropped).
/// Multi-consumer scenarios allocate a fresh host per <c>StartConsumingAsync</c> call.
/// </summary>
public sealed class RabbitMqConsumerHostRestartCycleTests
{
    [Fact]
    public async Task PrepareAsync_SecondCall_ThrowsInvalidOperationException()
    {
        var host = BuildHost();

        await host.PrepareAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "restart-q");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.PrepareAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "restart-q"));

        Assert.Contains("single-use", ex.Message);

        await host.DisposeAsync();
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static RabbitMqConsumerHost BuildHost()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());
        transport.SetupGet(t => t.GracefulShutdownTimeoutMilliseconds).Returns(1000);
        transport.SetupGet(t => t.MaxRetries).Returns(0);

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("restart-q");
        queueConfig.SetupGet(q => q.DisableErrors).Returns(false);
        queueConfig.SetupGet(q => q.ErrorQueueName).Returns("errors");
        queueConfig.SetupGet(q => q.AuditingEnabled).Returns(false);

        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);
        busConfig.SetupGet(b => b.DeadLetterUnhandledMessages).Returns(false);

        var channel = new Mock<IChannel>(MockBehavior.Loose);
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.BasicQosAsync(
                It.IsAny<uint>(), It.IsAny<ushort>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.SetupAdd(c => c.ChannelShutdownAsync += It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());
        channel.SetupRemove(c => c.ChannelShutdownAsync -= It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());

        var conn = new Mock<IServiceConnectConnection>();
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel.Object);
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel.Object);
        conn.SetupGet(c => c.UnderlyingConnection).Returns((global::RabbitMQ.Client.IConnection?)null);

        var retryHandler = new MessageRetryHandler(0, "errors", "restart-q", NullLogger.Instance);
        var auditPublisher = new MessageAuditPublisher(queueConfig.Object);
        var admissionGate = new RabbitMqAdmissionGate("restart-q");

        return new RabbitMqConsumerHost(
            conn.Object, transport.Object, queueConfig.Object, busConfig.Object,
            retryHandler, admissionGate, auditPublisher, NullLogger.Instance);
    }
}
