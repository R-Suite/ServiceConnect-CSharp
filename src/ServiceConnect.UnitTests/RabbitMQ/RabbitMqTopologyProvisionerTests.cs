using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using ServiceConnect.Client.RabbitMQ;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public class RabbitMqTopologyProvisionerTests
{
    private static Mock<IChannel> MockChannel()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.ExchangeDeclareAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>?>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.Setup(c => c.QueueDeclareAsync(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>?>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueueDeclareOk("q", 0, 0));
        channel.Setup(c => c.QueueBindAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IDictionary<string, object?>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return channel;
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenLoggerIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new RabbitMqTopologyProvisioner(null!));
    }

    [Fact]
    public async Task ConfigureDeclareExchangeAsync_DeclaresExchange_WithCorrectParameters()
    {
        var channel = MockChannel();
        var provisioner = new RabbitMqTopologyProvisioner(NullLogger.Instance);

        await provisioner.ConfigureDeclareExchangeAsync(channel.Object, "test.exchange", ExchangeType.Fanout);

        channel.Verify(c => c.ExchangeDeclareAsync(
            "test.exchange", ExchangeType.Fanout, true, false, null, false, false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ConfigureDeclareExchangeAsync_RethrowsOperationInterruptedException_RegardlessOfIsInitialSetup()
    {
        var channel = MockChannel();
        channel.Setup(c => c.ExchangeDeclareAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>?>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationInterruptedException(new ShutdownEventArgs(
                ShutdownInitiator.Library, 406, "PRECONDITION_FAILED", cause: null, cancellationToken: CancellationToken.None)));

        var provisioner = new RabbitMqTopologyProvisioner(NullLogger.Instance);

        // Channel-closing AMQP errors must always propagate so the caller can recreate
        // the channel rather than continue with a dead one — isInitialSetup is preserved
        // for source-compat but no longer suppresses the throw.
        await Assert.ThrowsAsync<OperationInterruptedException>(() =>
            provisioner.ConfigureDeclareExchangeAsync(channel.Object, "test.exchange", ExchangeType.Fanout, isInitialSetup: false));
    }

    [Fact]
    public async Task ConfigureDeclareExchangeAsync_RethrowsOperationInterruptedException_WhenInitialSetup()
    {
        var channel = MockChannel();
        var shutdownArgs = new ShutdownEventArgs(ShutdownInitiator.Library, 406, "PRECONDITION_FAILED", cause: null, cancellationToken: CancellationToken.None);
        channel.Setup(c => c.ExchangeDeclareAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>?>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationInterruptedException(shutdownArgs));

        var provisioner = new RabbitMqTopologyProvisioner(NullLogger.Instance);

        await Assert.ThrowsAsync<OperationInterruptedException>(() =>
            provisioner.ConfigureDeclareExchangeAsync(channel.Object, "test.exchange", ExchangeType.Fanout, isInitialSetup: true));
    }

    [Fact]
    public async Task ConfigureDeclareQueueAsync_DeclaresQueue_WithCorrectParameters()
    {
        var channel = MockChannel();
        var provisioner = new RabbitMqTopologyProvisioner(NullLogger.Instance);
        var args = new Dictionary<string, object?>();

        await provisioner.ConfigureDeclareQueueAsync(channel.Object, "orders", true, false, false, args);

        channel.Verify(c => c.QueueDeclareAsync(
            "orders", true, false, false, args, false, false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ConfigureDeclareQueueAsync_RethrowsOperationInterruptedException_WhenInitialSetup()
    {
        var channel = MockChannel();
        var shutdownArgs = new ShutdownEventArgs(ShutdownInitiator.Library, 406, "PRECONDITION_FAILED", cause: null, cancellationToken: CancellationToken.None);
        channel.Setup(c => c.QueueDeclareAsync(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>?>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationInterruptedException(shutdownArgs));

        var provisioner = new RabbitMqTopologyProvisioner(NullLogger.Instance);

        await Assert.ThrowsAsync<OperationInterruptedException>(() =>
            provisioner.ConfigureDeclareQueueAsync(channel.Object, "orders", true, false, false, new Dictionary<string, object?>(), isInitialSetup: true));
    }

    [Fact]
    public async Task ConfigureDeclareUtilityQueueAsync_DeclaresQueueAndExchangeAndBinds()
    {
        var channel = MockChannel();
        var provisioner = new RabbitMqTopologyProvisioner(NullLogger.Instance);
        var args = new Dictionary<string, object?>();

        await provisioner.ConfigureDeclareUtilityQueueAsync(channel.Object, "error.queue", args);

        channel.Verify(c => c.ExchangeDeclareAsync(
            "error.queue", ExchangeType.Direct, It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<IDictionary<string, object?>?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
        channel.Verify(c => c.QueueDeclareAsync(
            "error.queue", true, false, false, args, false, false, It.IsAny<CancellationToken>()),
            Times.Once);
        channel.Verify(c => c.QueueBindAsync(
            "error.queue", "error.queue", string.Empty, null, false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ConfigureDeclareUtilityQueueAsync_RethrowsOperationInterruptedException_RegardlessOfIsInitialSetup()
    {
        var channel = MockChannel();
        channel.Setup(c => c.ExchangeDeclareAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>?>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationInterruptedException(new ShutdownEventArgs(
                ShutdownInitiator.Library, 406, "PRECONDITION_FAILED", cause: null, cancellationToken: CancellationToken.None)));

        var provisioner = new RabbitMqTopologyProvisioner(NullLogger.Instance);
        var args = new Dictionary<string, object?>();

        await Assert.ThrowsAsync<OperationInterruptedException>(() =>
            provisioner.ConfigureDeclareUtilityQueueAsync(channel.Object, "error.queue", args, isInitialSetup: false));
    }

    [Fact]
    public async Task ConfigureDeclareUtilityQueueAsync_RethrowsOperationInterruptedException_WhenInitialSetup()
    {
        var channel = MockChannel();
        var shutdownArgs = new ShutdownEventArgs(ShutdownInitiator.Library, 406, "PRECONDITION_FAILED", cause: null, cancellationToken: CancellationToken.None);
        channel.Setup(c => c.QueueDeclareAsync(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>?>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationInterruptedException(shutdownArgs));

        var provisioner = new RabbitMqTopologyProvisioner(NullLogger.Instance);
        var args = new Dictionary<string, object?>();

        await Assert.ThrowsAsync<OperationInterruptedException>(() =>
            provisioner.ConfigureDeclareUtilityQueueAsync(channel.Object, "error.queue", args, isInitialSetup: true));
    }

    [Fact]
    public async Task ConfigureRetryTopologyAsync_DeclaresRetryTopology()
    {
        var channel = MockChannel();
        var provisioner = new RabbitMqTopologyProvisioner(NullLogger.Instance);
        var args = new Dictionary<string, object?>();

        await provisioner.ConfigureRetryTopologyAsync(channel.Object, "orders", true, false, 1234, args);

        channel.Verify(c => c.ExchangeDeclareAsync(
            "orders" + RabbitMqQueueNaming.RetryDeadLetterExchangeSuffix, ExchangeType.Direct, true, false, null, false, false, It.IsAny<CancellationToken>()),
            Times.Once);
        channel.Verify(c => c.QueueBindAsync(
            "orders", "orders" + RabbitMqQueueNaming.RetryDeadLetterExchangeSuffix, "orders" + RabbitMqQueueNaming.RetryQueueSuffix, null, false, It.IsAny<CancellationToken>()),
            Times.Once);
        channel.Verify(c => c.QueueDeclareAsync(
            "orders" + RabbitMqQueueNaming.RetryQueueSuffix,
            true,
            false,
            false,
            It.Is<IDictionary<string, object?>?>(d =>
                d != null &&
                Equals(d[RabbitMqQueueNaming.XDeadLetterExchangeArgument], "orders" + RabbitMqQueueNaming.RetryDeadLetterExchangeSuffix) &&
                Equals(d[RabbitMqQueueNaming.XMessageTtlArgument], 1234)),
            false,
            false,
            It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
