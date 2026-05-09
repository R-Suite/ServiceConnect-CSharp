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
    public async Task StartConsumingAsync_WhenHostStartupFails_EagerlyDisposesPartialHostsAndClearsClients()
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
        // consumer channel inside RabbitMqConsumerHost.PrepareAsync — throws.
        // The catch block must eagerly dispose any partially-built host and drain
        // _clients so the caller can retry without calling DisposeAsync first.
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

        // The catch block disposes partial hosts eagerly and drains _clients, so
        // _clients is empty after a failure — no DisposeAsync call required to recover.
        var clientsField = typeof(Consumer).GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(clientsField);
        var clients = (System.Collections.ICollection)clientsField!.GetValue(consumer)!;
        Assert.Empty(clients);

        // _started must be reset to 0 so a subsequent StartConsumingAsync can retry.
        var startedField = typeof(Consumer).GetField("_started", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(startedField);
        Assert.Equal(0, (int)startedField!.GetValue(consumer)!);
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
    // Consumer.DisposeAsync exception resilience
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
    // Consumer.StartConsumingAsync idempotency
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

    // -----------------------------------------------------------------------
    // Accept any IDictionary/IReadOnlyDictionary for queue Arguments
    // -----------------------------------------------------------------------

    [Fact]
    public void Ctor_ArgumentsAsReadOnlyDictionary_DoesNotThrow()
    {
        // The ctor must accept any IDictionary/IReadOnlyDictionary shape for
        // settings[Arguments]; a direct cast to Dictionary<,> would fail on
        // ReadOnlyDictionary callers.
        var inner = new Dictionary<string, object?> { ["x-message-ttl"] = 60_000 };
        var readOnly = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(inner);

        var cfg = new Mock<ITransportConfiguration>();
        cfg.SetupGet(c => c.MaxRetries).Returns(3);
        cfg.SetupGet(c => c.RetryDelay).Returns(1000);
        cfg.SetupGet(c => c.ClientSettings).Returns(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.Arguments] = readOnly,
        });

        var ex = Record.Exception(() => new Consumer(
            cfg.Object,
            new Mock<IQueueConfiguration>().Object,
            new Mock<IBusConfiguration>().Object,
            NullLogger<Consumer>.Instance));
        Assert.Null(ex);
    }

    [Fact]
    public void Ctor_ArgumentsAsNonDictionaryValue_ThrowsInvalidOperationException()
    {
        // Non-dictionary values must produce a clear InvalidOperationException rather
        // than an opaque InvalidCastException.
        var cfg = new Mock<ITransportConfiguration>();
        cfg.SetupGet(c => c.MaxRetries).Returns(3);
        cfg.SetupGet(c => c.RetryDelay).Returns(1000);
        cfg.SetupGet(c => c.ClientSettings).Returns(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.Arguments] = "not-a-dictionary",
        });

        Assert.Throws<InvalidOperationException>(() => new Consumer(
            cfg.Object,
            new Mock<IQueueConfiguration>().Object,
            new Mock<IBusConfiguration>().Object,
            NullLogger<Consumer>.Instance));
    }
}
