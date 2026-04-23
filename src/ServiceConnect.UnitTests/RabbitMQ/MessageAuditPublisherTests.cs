using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Verifies the post-fix behaviour of <see cref="MessageAuditPublisher"/>:
/// routing key is always forced to <see cref="string.Empty"/> regardless of the
/// configured <c>AuditRoutingKey</c>, matching the empty-key binding of the audit
/// direct exchange.
/// </summary>
public class MessageAuditPublisherRoutingKeyTests
{
    private static BasicDeliverEventArgs MakeArgs()
    {
        var props = new BasicProperties();
        return new BasicDeliverEventArgs("tag", 1, false, "", "q", props, new byte[] { 1, 2, 3 });
    }

    private static Mock<IQueueConfiguration> MakeQueueCfg(string auditRoutingKey)
    {
        var cfg = new Mock<IQueueConfiguration>();
        cfg.SetupGet(c => c.AuditingEnabled).Returns(true);
        cfg.SetupGet(c => c.AuditQueueName).Returns("audit");
        cfg.SetupGet(c => c.AuditRoutingKey).Returns(auditRoutingKey);
        return cfg;
    }

    /// <summary>
    /// When <c>AuditRoutingKey</c> is set to a non-empty value the publish MUST still
    /// use <c>routingKey=""</c> because the audit direct exchange is bound with an empty key.
    /// A non-empty routing key would be unroutable with <c>mandatory=false</c> → silent drop.
    /// </summary>
    [Fact]
    public async Task PublishAuditIfEnabledAsync_WithNonEmptyRoutingKey_UsesEmptyRoutingKey()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var publisher = new MessageAuditPublisher(MakeQueueCfg("telemetry.v1").Object);
        var headers = new Dictionary<string, object> { [HeaderKeys.MessageType] = "SomeMessage" };

        await publisher.PublishAuditIfEnabledAsync(channel.Object, MakeArgs(), headers);

        // Post-fix: routingKey must be "" regardless of AuditRoutingKey configuration.
        channel.Verify(c => c.BasicPublishAsync(
            "audit",
            string.Empty, // forced to empty to match audit exchange binding
            false,
            It.IsAny<BasicProperties>(),
            It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
