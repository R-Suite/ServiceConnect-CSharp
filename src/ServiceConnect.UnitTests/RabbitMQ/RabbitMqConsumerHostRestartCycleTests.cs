using System.Reflection;
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
/// Verifies that PrepareAsync resets _disposeStarted so a Prepare→Dispose→Prepare→Dispose
/// cycle runs the full DisposeAsync teardown on each Dispose rather than short-circuiting
/// at the CAS guard after the first cycle.
/// </summary>
public sealed class RabbitMqConsumerHostRestartCycleTests
{
    [Fact]
    public async Task PrepareAsync_AfterPriorDispose_ResetsDisposeStartedFlag()
    {
        // Drive Prepare → Dispose, then read _disposeStarted via reflection — pre-fix this is 1
        // (DisposeAsync's CAS-set), post-fix the next PrepareAsync resets to 0 so a subsequent
        // DisposeAsync can run the full teardown rather than CAS-short-circuiting.
        var host = BuildHost();

        await host.PrepareAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "restart-q");
        await host.DisposeAsync();

        var disposeStartedField = typeof(RabbitMqConsumerHost).GetField(
            "_disposeStarted", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Equal(1, (int)disposeStartedField!.GetValue(host)!);

        // The fix: a second Prepare resets the flag.
        await host.PrepareAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "restart-q");
        Assert.Equal(0, (int)disposeStartedField.GetValue(host)!);

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
        queueConfig.SetupGet(q => q.AuditRoutingKey).Returns(string.Empty);

        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);
        busConfig.SetupGet(b => b.DeadLetterUnhandledMessages).Returns(false);

        // Each call to PrepareAsync creates new consumer + publish channels. Loose mock so
        // any call not explicitly set up returns a default rather than throwing.
        var channel = new Mock<IChannel>(MockBehavior.Loose);
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.BasicQosAsync(
                It.IsAny<uint>(), It.IsAny<ushort>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // ChannelShutdownAsync subscription happens in PrepareAsync; unsubscription in DisposeAsync.
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
