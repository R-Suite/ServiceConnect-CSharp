using Microsoft.Extensions.Logging;
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
/// Audit publish is best-effort: a failure publishing the audit copy must not propagate
/// into the consumer pipeline (which would nack-with-requeue and re-run the handler) and
/// must not throw on the cancellation path.
/// </summary>
public class MessageAuditPublisherSwallowFailureTests
{
    private static BasicDeliverEventArgs MakeArgs()
    {
        var props = new BasicProperties();
        return new BasicDeliverEventArgs("tag", 1, false, "", "q", props, new byte[] { 1, 2, 3 });
    }

    private static Mock<IQueueConfiguration> MakeQueueCfg()
    {
        var cfg = new Mock<IQueueConfiguration>();
        cfg.SetupGet(c => c.AuditingEnabled).Returns(true);
        cfg.SetupGet(c => c.AuditQueueName).Returns("audit");
        cfg.SetupGet(c => c.AuditRoutingKey).Returns(string.Empty);
        return cfg;
    }

    [Fact]
    public async Task PublishAuditIfEnabledAsync_BasicPublishThrows_SwallowsAndLogsWarning()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broker quota exceeded"));

        var loggerMock = new Mock<ILogger<MessageAuditPublisher>>();
        var publisher = new MessageAuditPublisher(MakeQueueCfg().Object, loggerMock.Object);
        var headers = new Dictionary<string, object> { [HeaderKeys.MessageType] = "SomeMessage" };

        // Must not throw — audit failure is swallowed.
        await publisher.PublishAuditIfEnabledAsync(channel.Object, MakeArgs(), headers);

        loggerMock.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Audit publish failed")),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task PublishAuditIfEnabledAsync_BasicPublishThrowsOperationCanceled_Propagates()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var publisher = new MessageAuditPublisher(MakeQueueCfg().Object, NullLogger<MessageAuditPublisher>.Instance);
        var headers = new Dictionary<string, object> { [HeaderKeys.MessageType] = "SomeMessage" };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => publisher.PublishAuditIfEnabledAsync(channel.Object, MakeArgs(), headers));
    }
}
