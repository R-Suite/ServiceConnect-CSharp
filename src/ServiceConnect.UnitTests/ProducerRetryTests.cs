using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

public class ProducerRetryTests
{
    private static Producer CreateProducer()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.RetryCount] = (ushort)2,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)0,
        });

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        return new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);
    }

    // Stamps the connection-lifecycle properties read by ProducerConnection's source-gen
    // log emit (ProducerConnectionOpened) onto a strict IConnection mock. Without these
    // setups the strict mock would throw on Endpoint/ClientProvidedName access from
    // ResolveEndpoint, masking the actual test scenario as an "unexpected invocation".
    private static void StubLifecycleSurface(Mock<IConnection> connection)
    {
        connection.SetupGet(c => c.Endpoint).Returns(new AmqpTcpEndpoint("localhost", 5672));
        connection.SetupGet(c => c.ClientProvidedName).Returns("test");
    }

    private static void SetField<T>(Producer producer, string fieldName, T value) =>
        ProducerInternals.SetField(producer, fieldName, value);

    private static T GetField<T>(Producer producer, string fieldName) =>
        ProducerInternals.GetField<T>(producer, fieldName);

    [Fact]
    public async Task PublishAsync_WhenFirstPublishFails_ReconnectsAndRetriesSuccessfully()
    {
        // The publish-side retry triggers the reset-required flag, and the next attempt's
        // EnsureConnectedAsync drives the recreate via CreateConnectionAsync. The test seam
        // therefore uses CreateConnectionForTests: the second connection serves the second
        // (healthy) channel.
        var producer = CreateProducer();
        var firstChannel = new Mock<IChannel>();
        firstChannel.SetupGet(c => c.IsOpen).Returns(true);
        var secondChannel = new Mock<IChannel>();
        secondChannel.SetupGet(c => c.IsOpen).Returns(true);
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;

        firstChannel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("first publish failed"));

        secondChannel.Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        secondChannel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        SetField(producer, "_model", firstChannel.Object);
        SetField(producer, "_connected", true);

        var secondConnection = new Mock<IConnection>();
        StubLifecycleSurface(secondConnection);
        secondConnection.SetupGet(c => c.IsOpen).Returns(true);
        secondConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(secondChannel.Object);
        producer.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(secondConnection.Object);

        await producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 });

        firstChannel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        secondChannel.Verify(c => c.ExchangeDeclareAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<IDictionary<string, object?>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Once);
        secondChannel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WhenPublishIsCanceled_DoesNotReconnectOrRetry()
    {
        var producer = CreateProducer();
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;
        using var cancellationSource = new CancellationTokenSource();
        var cancellationToken = cancellationSource.Token;
        var connectionAttempts = 0;

        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("publish canceled", cancellationToken));

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);
        // The reconnect-on-retry path goes through CreateConnectionAsync; the assertion that
        // cancellation does NOT trigger a retry shifts to counting connect attempts. OCE
        // remains non-retriable so this seam should never fire.
        producer.CreateConnectionForTests = (_, _, _, _) =>
        {
            Interlocked.Increment(ref connectionAttempts);
            var conn = new Mock<IConnection>();
            StubLifecycleSurface(conn);
            conn.SetupGet(c => c.IsOpen).Returns(true);
            conn.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(channel.Object);
            return Task.FromResult(conn.Object);
        };

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }, cancellationToken: cancellationToken));

        Assert.Equal(cancellationToken, ex.CancellationToken);
        Assert.Equal(0, connectionAttempts);
        channel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WhenCanceledDuringReconnect_StopsRetryingPromptly()
    {
        // The slow-reconnect simulation runs through CreateConnectionForTests: when the
        // first publish fails, the next attempt's EnsureConnectedAsync drives
        // CreateConnectionAsync, which we hang on the caller token to verify cancellation
        // propagates promptly.
        var producer = CreateProducer();
        var firstChannel = new Mock<IChannel>();
        firstChannel.SetupGet(c => c.IsOpen).Returns(true);
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;
        using var cancellationSource = new CancellationTokenSource();
        var cancellationToken = cancellationSource.Token;
        var reconnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionAttempts = 0;

        firstChannel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("first publish failed"));

        SetField(producer, "_model", firstChannel.Object);
        SetField(producer, "_connected", true);
        producer.CreateConnectionForTests = async (_, _, _, ct) =>
        {
            Interlocked.Increment(ref connectionAttempts);
            reconnectStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        };

        var publishTask = producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }, cancellationToken: cancellationToken);

        await reconnectStarted.Task;
        cancellationSource.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publishTask);

        Assert.Equal(cancellationToken, ex.CancellationToken);
        Assert.Equal(1, connectionAttempts);
        firstChannel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WhenCanceledDuringInitialConnect_StopsPromptly()
    {
        var producer = CreateProducer();
        var connectionSemaphore = GetField<SemaphoreSlim>(producer, "_connectionSemaphore");
        using var cancellationSource = new CancellationTokenSource();
        var cancellationToken = cancellationSource.Token;

        await connectionSemaphore.WaitAsync();

        try
        {
            var publishTask = producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }, cancellationToken: cancellationToken);

            cancellationSource.Cancel();

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publishTask);

            Assert.Equal(cancellationToken, ex.CancellationToken);
        }
        finally
        {
            connectionSemaphore.Release();
        }
    }

    [Fact]
    public async Task PublishAsync_WhenInitialCreateChannelFails_RetriesAndSucceedsWithoutHanging()
    {
        var producer = CreateProducer();
        var firstConnection = new Mock<IConnection>(MockBehavior.Strict);
        var secondConnection = new Mock<IConnection>(MockBehavior.Strict);
        var secondChannel = new Mock<IChannel>(MockBehavior.Strict);
        var connectionAttempts = 0;
        StubLifecycleSurface(firstConnection);
        StubLifecycleSurface(secondConnection);

        firstConnection.SetupGet(c => c.IsOpen).Returns(true);
        firstConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("first channel create failed"));
        firstConnection
            .Setup(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        firstConnection.Setup(c => c.Dispose());

        secondConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(secondChannel.Object);

        secondChannel.Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        secondChannel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        producer.CreateConnectionForTests = (_, _, _, _) =>
        {
            connectionAttempts++;
            return Task.FromResult(connectionAttempts == 1 ? firstConnection.Object : secondConnection.Object);
        };

        await producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, connectionAttempts);
        firstConnection.Verify(c => c.CloseAsync(
            It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        firstConnection.Verify(c => c.Dispose(), Times.Once);
        secondChannel.Verify(c => c.ExchangeDeclareAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<IDictionary<string, object?>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Once);
        secondChannel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WhenCreateChannelIsCanceled_DisposesPartialConnectionAndClearsConnectionState()
    {
        var producer = CreateProducer();
        var connection = new Mock<IConnection>(MockBehavior.Strict);
        using var cancellationSource = new CancellationTokenSource();
        var cancellationToken = cancellationSource.Token;
        StubLifecycleSurface(connection);

        connection.SetupGet(c => c.IsOpen).Returns(true);
        connection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.Is<CancellationToken>(ct => ct == cancellationToken)))
            .ThrowsAsync(new OperationCanceledException("create channel canceled", cancellationToken));
        connection
            .Setup(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        connection.Setup(c => c.Dispose());

        producer.CreateConnectionForTests = (_, _, _, ct) =>
        {
            Assert.Equal(cancellationToken, ct);
            return Task.FromResult(connection.Object);
        };

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }, cancellationToken: cancellationToken));

        Assert.Equal(cancellationToken, ex.CancellationToken);
        Assert.Null(GetField<IConnection?>(producer, "_connection"));
        Assert.False(GetField<bool>(producer, "_connected"));
        connection.Verify(c => c.CloseAsync(
            It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(c => c.Dispose(), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WhenCreateChannelIsCanceled_UsesCallerTokenAtConnectBoundary()
    {
        var producer = CreateProducer();
        var connection = new Mock<IConnection>(MockBehavior.Strict);
        using var cancellationSource = new CancellationTokenSource();
        var cancellationToken = cancellationSource.Token;
        CancellationToken? createConnectionToken = null;
        var createChannelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StubLifecycleSurface(connection);

        connection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .Returns<CreateChannelOptions?, CancellationToken>(async (_, ct) =>
            {
                createChannelStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("unreachable");
            });

        producer.CreateConnectionForTests = (_, _, _, ct) =>
        {
            createConnectionToken = ct;
            return Task.FromResult(connection.Object);
        };

        var publishTask = producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }, cancellationToken: cancellationToken);

        await createChannelStarted.Task;
        cancellationSource.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publishTask);

        Assert.Equal(cancellationToken, createConnectionToken);
        Assert.Equal(cancellationToken, ex.CancellationToken);
        connection.Verify(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.Is<CancellationToken>(ct => ct == cancellationToken)), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WhenExchangeDeclareFails_ReconnectsAndRetriesSuccessfully()
    {
        // Migrated from ReconnectForTests to CreateConnectionForTests: see
        // PublishAsync_WhenFirstPublishFails for the rationale.
        var producer = CreateProducer();
        var firstChannel = new Mock<IChannel>();
        firstChannel.SetupGet(c => c.IsOpen).Returns(true);
        var secondChannel = new Mock<IChannel>();
        secondChannel.SetupGet(c => c.IsOpen).Returns(true);

        firstChannel.Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("declare failed"));

        secondChannel.Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        secondChannel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        SetField(producer, "_model", firstChannel.Object);
        SetField(producer, "_connected", true);

        var secondConnection = new Mock<IConnection>();
        StubLifecycleSurface(secondConnection);
        secondConnection.SetupGet(c => c.IsOpen).Returns(true);
        secondConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(secondChannel.Object);
        producer.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(secondConnection.Object);

        await producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 });

        secondChannel.Verify(c => c.ExchangeDeclareAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<IDictionary<string, object?>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Once);
        secondChannel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WhenExchangeDeclareIsCanceled_DoesNotReconnectOrRetry()
    {
        var producer = CreateProducer();
        var channel = new Mock<IChannel>(MockBehavior.Strict);
        channel.SetupGet(c => c.IsOpen).Returns(true);
        using var cancellationSource = new CancellationTokenSource();
        var cancellationToken = cancellationSource.Token;
        var connectionAttempts = 0;

        channel.Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.Is<CancellationToken>(ct => ct == cancellationToken)))
            .ThrowsAsync(new OperationCanceledException("declare canceled", cancellationToken));

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);
        // Migrated to CreateConnectionForTests: counts retry-driven reconnect attempts.
        producer.CreateConnectionForTests = (_, _, _, _) =>
        {
            Interlocked.Increment(ref connectionAttempts);
            var conn = new Mock<IConnection>();
            StubLifecycleSurface(conn);
            conn.SetupGet(c => c.IsOpen).Returns(true);
            conn.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(channel.Object);
            return Task.FromResult(conn.Object);
        };

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }, cancellationToken: cancellationToken));

        Assert.Equal(cancellationToken, ex.CancellationToken);
        Assert.Equal(0, connectionAttempts);
        channel.Verify(c => c.ExchangeDeclareAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<IDictionary<string, object?>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.Is<CancellationToken>(ct => ct == cancellationToken)), Times.Once);
    }

    [Fact]
    public async Task SendAsync_ByEndpoint_WhenCreateConnectionThrowsOnFirstAttempt_RecoversAndPublishes()
    {
        // Gap 3: CreateConnectionAsync throws on the first attempt, succeeds on the second.
        // The producer must transparently recover via the built-in retry loop and the
        // publish must eventually succeed — no exception surfaces to the caller.
        var producer = CreateProducer();
        var firstConnection = new Mock<IConnection>(MockBehavior.Strict);
        var secondConnection = new Mock<IConnection>(MockBehavior.Strict);
        var secondChannel = new Mock<IChannel>(MockBehavior.Strict);
        var connectionAttempts = 0;
        StubLifecycleSurface(firstConnection);
        StubLifecycleSurface(secondConnection);

        firstConnection.SetupGet(c => c.IsOpen).Returns(true);
        firstConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("channel create failed on first attempt"));
        firstConnection
            .Setup(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        firstConnection.Setup(c => c.Dispose());

        secondConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(secondChannel.Object);

        secondChannel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        producer.CreateConnectionForTests = (_, _, _, _) =>
        {
            connectionAttempts++;
            return Task.FromResult(connectionAttempts == 1 ? firstConnection.Object : secondConnection.Object);
        };

        await producer.SendAsync("target-endpoint", typeof(object), new byte[] { 1, 2, 3 })
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, connectionAttempts);
        secondChannel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendAsync_ByEndpoint_WhenFirstPublishFails_ReconnectsAndRetriesSuccessfully()
    {
        // Migrated from ReconnectForTests to CreateConnectionForTests.
        var producer = CreateProducer();
        var firstChannel = new Mock<IChannel>();
        firstChannel.SetupGet(c => c.IsOpen).Returns(true);
        var secondChannel = new Mock<IChannel>();
        secondChannel.SetupGet(c => c.IsOpen).Returns(true);

        firstChannel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("first publish failed"));

        secondChannel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        SetField(producer, "_model", firstChannel.Object);
        SetField(producer, "_connected", true);

        var secondConnection = new Mock<IConnection>();
        StubLifecycleSurface(secondConnection);
        secondConnection.SetupGet(c => c.IsOpen).Returns(true);
        secondConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(secondChannel.Object);
        producer.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(secondConnection.Object);

        await producer.SendAsync("endpoint", typeof(object), new byte[] { 1, 2, 3 });

        secondChannel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
