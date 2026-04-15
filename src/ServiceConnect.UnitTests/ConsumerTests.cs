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
}
