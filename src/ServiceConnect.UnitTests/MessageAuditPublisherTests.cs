using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MessageAuditPublisherTests
{
    private static BasicDeliverEventArgs MakeArgs()
    {
        var props = new BasicProperties();
        return new BasicDeliverEventArgs("tag", 1, false, "", "q", props, new byte[] { 1, 2, 3 });
    }

    private static Mock<IQueueConfiguration> MakeQueueCfg(
        bool auditingEnabled,
        string auditExchange = "audit",
        string auditRoutingKey = "")
    {
        var cfg = new Mock<IQueueConfiguration>();
        cfg.SetupGet(c => c.AuditingEnabled).Returns(auditingEnabled);
        cfg.SetupGet(c => c.AuditQueueName).Returns(auditExchange);
        cfg.SetupGet(c => c.AuditRoutingKey).Returns(auditRoutingKey);
        return cfg;
    }

    [Fact]
    public async Task PublishAuditIfEnabledAsync_Publishes_WhenAuditingEnabled()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var publisher = new MessageAuditPublisher(MakeQueueCfg(true).Object);
        var headers = new Dictionary<string, object> { [HeaderKeys.MessageType] = "SomeMessage" };

        await publisher.PublishAuditIfEnabledAsync(channel.Object, MakeArgs(), headers);

        channel.Verify(c => c.BasicPublishAsync(
            "audit", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAuditIfEnabledAsync_Skips_WhenAuditingDisabled()
    {
        var channel = new Mock<IChannel>();
        var publisher = new MessageAuditPublisher(MakeQueueCfg(false).Object);

        await publisher.PublishAuditIfEnabledAsync(channel.Object, MakeArgs(), []);

        channel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PublishAuditIfEnabledAsync_Skips_ForByteStreamMessageType()
    {
        var channel = new Mock<IChannel>();
        var publisher = new MessageAuditPublisher(MakeQueueCfg(true).Object);
        var headers = new Dictionary<string, object> { [HeaderKeys.MessageType] = HeaderKeys.ByteStream };

        await publisher.PublishAuditIfEnabledAsync(channel.Object, MakeArgs(), headers);

        channel.Verify(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PublishAuditIfEnabledAsync_AlwaysUsesEmptyRoutingKey_EvenWhenConfigured()
    {
        // AuditRoutingKey is ignored at publish time: the audit direct exchange is bound
        // with an empty routing key, so a non-empty value would cause a silent drop.
        // The fix forces routingKey="" regardless of the configured value.
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var publisher = new MessageAuditPublisher(MakeQueueCfg(true, auditRoutingKey: "audit.orders").Object);
        var headers = new Dictionary<string, object> { [HeaderKeys.MessageType] = "SomeMessage" };

        await publisher.PublishAuditIfEnabledAsync(channel.Object, MakeArgs(), headers);

        channel.Verify(c => c.BasicPublishAsync(
            "audit", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAuditIfEnabledAsync_Publishes_WhenMessageTypeHeaderAbsent()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var publisher = new MessageAuditPublisher(MakeQueueCfg(true).Object);

        await publisher.PublishAuditIfEnabledAsync(channel.Object, MakeArgs(), []);

        channel.Verify(c => c.BasicPublishAsync(
            "audit", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
