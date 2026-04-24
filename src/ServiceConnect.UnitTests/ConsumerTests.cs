using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

public class ConsumerTests
{
    private static Mock<ITransportConfiguration> MakeTransportCfg()
    {
        var cfg = new Mock<ITransportConfiguration>();
        cfg.SetupGet(c => c.MaxRetries).Returns(3);
        cfg.SetupGet(c => c.RetryDelay).Returns(1000);
        cfg.SetupGet(c => c.ClientSettings).Returns(new Dictionary<string, object>());
        return cfg;
    }

    private static Mock<IQueueConfiguration> MakeQueueCfg()
    {
        var cfg = new Mock<IQueueConfiguration>();
        cfg.SetupGet(c => c.QueueName).Returns("q");
        cfg.SetupGet(c => c.ErrorQueueName).Returns("err");
        cfg.SetupGet(c => c.AuditQueueName).Returns("audit");
        cfg.SetupGet(c => c.PurgeQueueOnStartup).Returns(false);
        cfg.SetupGet(c => c.AuditingEnabled).Returns(false);
        return cfg;
    }

    private static Mock<IBusConfiguration> MakeBusCfg()
    {
        var cfg = new Mock<IBusConfiguration>();
        cfg.SetupGet(c => c.ConsumerCount).Returns(1);
        return cfg;
    }

    [Fact]
    public async Task StartConsumingAsync_WhenInitialTopologySetupFails_RethrowsAndDisposesSetupChannel()
    {
        var shutdownArgs = new ShutdownEventArgs(ShutdownInitiator.Library, 406, "PRECONDITION_FAILED", cause: null, cancellationToken: CancellationToken.None);
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationInterruptedException(shutdownArgs));
        channel.Setup(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var connection = new Mock<IServiceConnectConnection>();
        connection.Setup(c => c.CreateChannelAsync()).ReturnsAsync(channel.Object);

        var consumer = new Consumer(
            MakeTransportCfg().Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            NullLogger<Consumer>.Instance,
            connection.Object);

        await Assert.ThrowsAsync<OperationInterruptedException>(() =>
            consumer.StartConsumingAsync("q", ["MessageType"], (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true })));

        channel.Verify(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.Dispose(), Times.Once);
    }

    [Fact]
    public async Task StartConsumingAsync_WhenInitialQueueDeclarationFails_RethrowsAndDisposesSetupChannel()
    {
        var shutdownArgs = new ShutdownEventArgs(ShutdownInitiator.Library, 406, "PRECONDITION_FAILED", cause: null, cancellationToken: CancellationToken.None);
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.Setup(c => c.QueueDeclareAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationInterruptedException(shutdownArgs));
        channel.Setup(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var connection = new Mock<IServiceConnectConnection>();
        connection.Setup(c => c.CreateChannelAsync()).ReturnsAsync(channel.Object);

        var consumer = new Consumer(
            MakeTransportCfg().Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            NullLogger<Consumer>.Instance,
            connection.Object);

        await Assert.ThrowsAsync<OperationInterruptedException>(() =>
            consumer.StartConsumingAsync("q", ["MessageType"], (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true })));

        channel.Verify(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.Dispose(), Times.Once);
    }

    [Fact]
    public async Task StartConsumingAsync_WhenHostStartupFails_RegistersHostBeforeStartForDisposeToReach()
    {
        var shutdownArgs = new ShutdownEventArgs(ShutdownInitiator.Library, 406, "PRECONDITION_FAILED", cause: null, cancellationToken: CancellationToken.None);

        var setupChannel = new Mock<IChannel>();
        setupChannel.Setup(c => c.IsOpen).Returns(true);
        setupChannel.Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        setupChannel.Setup(c => c.QueueDeclareAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueueDeclareOk("q", 0, 0));
        setupChannel.Setup(c => c.QueueBindAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        setupChannel.Setup(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var connection = new Mock<IServiceConnectConnection>();
        // First call — Consumer's setup channel — succeeds. Second call — host's
        // consumer channel inside RabbitMqConsumerHost.StartConsumingAsync — throws.
        // The host must be registered in _clients before StartConsumingAsync runs
        // so a mid-startup failure still leaves it trackable for shutdown/disposal
        // rather than leaking the partially-initialised instance.
        connection.SetupSequence(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(setupChannel.Object)
            .ThrowsAsync(new OperationInterruptedException(shutdownArgs));

        var transportCfg = MakeTransportCfg();
        transportCfg.SetupGet(c => c.MaxRetries).Returns(0);
        var queueCfg = MakeQueueCfg();

        var consumer = new Consumer(
            transportCfg.Object,
            queueCfg.Object,
            MakeBusCfg().Object,
            NullLogger<Consumer>.Instance,
            connection.Object);

        await Assert.ThrowsAsync<OperationInterruptedException>(() =>
            consumer.StartConsumingAsync("q", ["MessageType"], (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true })));

        var clientsField = typeof(Consumer).GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(clientsField);
        var clients = (System.Collections.ICollection)clientsField!.GetValue(consumer)!;
        Assert.Single(clients);
    }

    [Fact]
    public async Task DisposeAsync_DoesNotDisposeCallerSuppliedConnection()
    {
        var connection = new Mock<IServiceConnectConnection>(MockBehavior.Strict);
        // Strict mock: any call other than what we set up fails the test. DisposeAsync
        // must not be invoked on a caller-owned connection.

        var consumer = new Consumer(
            MakeTransportCfg().Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            NullLogger<Consumer>.Instance,
            connection.Object);

        await consumer.DisposeAsync();

        connection.Verify(c => c.DisposeAsync(), Times.Never);
    }

    // -----------------------------------------------------------------------
    // M11 — Consumer.DisposeAsync exception resilience
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DisposeAsync_WhenMiddleHostThrows_StillDisposesAllOthers()
    {
        // Arrange: inject three IAsyncDisposable stubs into _clients via reflection.
        // hostB throws TimeoutException; hostA and hostC must still be disposed.
        // ConcurrentBag iteration order is unspecified, so asserting all three were
        // invoked proves "continues past failure" rather than "happened to dispose
        // others first".
        var hostA = new Mock<IAsyncDisposable>();
        hostA.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var hostB = new Mock<IAsyncDisposable>();
        hostB.Setup(h => h.DisposeAsync()).Throws(new TimeoutException("AMQP 0-9-1 channel closed"));
        var hostC = new Mock<IAsyncDisposable>();
        hostC.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var connection = new Mock<IServiceConnectConnection>(MockBehavior.Strict);

        var consumer = new Consumer(
            MakeTransportCfg().Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            NullLogger<Consumer>.Instance,
            connection.Object);

        // Inject via reflection — requires _clients to be ConcurrentBag<IAsyncDisposable>
        var clientsField = typeof(Consumer).GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(clientsField);
        var clients = (ConcurrentBag<IAsyncDisposable>)clientsField!.GetValue(consumer)!;
        clients.Add(hostA.Object);
        clients.Add(hostB.Object);
        clients.Add(hostC.Object);

        await consumer.DisposeAsync(); // must not throw

        hostA.Verify(h => h.DisposeAsync(), Times.Once);
        hostB.Verify(h => h.DisposeAsync(), Times.Once); // the failing one was still reached
        hostC.Verify(h => h.DisposeAsync(), Times.Once); // not leaked past the failure
    }

    // -----------------------------------------------------------------------
    // M12 — Consumer.StartConsumingAsync idempotency
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StartConsumingAsync_CalledTwice_ThrowsInvalidOperationException()
    {
        // A fully-configured Consumer that can complete the first StartConsumingAsync
        // call needs topology + a host channel. We use an approach where the second
        // call throws before any channel is created: the _started gate fires first.
        // So for this test we just need two calls; the first must succeed (or at least
        // advance past the gate) and the second must throw.
        //
        // The simplest harness: set _started directly via reflection to simulate the
        // already-consuming state, then confirm the second call throws.
        var connection = new Mock<IServiceConnectConnection>(MockBehavior.Strict);

        var consumer = new Consumer(
            MakeTransportCfg().Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            NullLogger<Consumer>.Instance,
            connection.Object);

        // Simulate "already started" by setting _started = 1 directly.
        var startedField = typeof(Consumer).GetField("_started", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(startedField);
        startedField!.SetValue(consumer, 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            consumer.StartConsumingAsync("q", ["MessageType"],
                (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true })));
    }
}
