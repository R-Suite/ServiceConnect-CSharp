using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class InboundMessageProcessorNullHandlerTests
{
    [Fact]
    public async Task ProcessAsync_NullConsumerEventHandler_AttachesSyntheticInvalidOperationException()
    {
        // Capture the exception JSON published to the error exchange. With maxRetries=0 the very
        // first failure routes immediately to the error exchange (no retry queue intermediate).

        BasicProperties? capturedProps = null;
        var channel = new Mock<IChannel>();
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (_, _, _, p, _, _) => capturedProps = p)
            .Returns(ValueTask.CompletedTask);

        var retryHandler = new MessageRetryHandler(maxRetries: 0, errorExchange: "error", NullLogger.Instance);
        var queueConfig = Mock.Of<IQueueConfiguration>(q => q.QueueName == "main-q");
        var auditPublisher = new MessageAuditPublisher(queueConfig);

        var processor = new InboundMessageProcessor(
            consumerEventHandler: null!,  // the case under test
            retryHandler: retryHandler,
            auditPublisher: auditPublisher,
            queueConfiguration: queueConfig,
            timeProvider: TimeProvider.System,
            logger: NullLogger.Instance,
            retryQueueName: "main-q.Retries",
            errorsDisabled: false,
            deadLetterUnhandledMessages: false,
            includeMachineNameInHeaders: false,
            shutdownTimedOut: () => false,
            shutdownPublishToken: () => CancellationToken.None);

        var args = new BasicDeliverEventArgs(
            consumerTag: "ct",
            deliveryTag: 1,
            redelivered: false,
            exchange: "",
            routingKey: "main-q",
            properties: new BasicProperties
            {
                Headers = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [HeaderKeys.FullTypeName] = "Foo.Bar",
                },
            },
            body: new byte[] { 1 });

        await processor.ProcessAsync(channel.Object, args, CancellationToken.None);

        Assert.NotNull(capturedProps);
        Assert.NotNull(capturedProps.Headers);
        Assert.True(capturedProps.Headers.TryGetValue(HeaderKeys.Exception, out var exJson));
        Assert.NotNull(exJson);
        Assert.IsType<string>(exJson);
        var json = (string)exJson;
        // Pre-fix: ex is null so the Exception header is never written to the error-exchange publish.
        // Post-fix: the JSON contains the synthetic InvalidOperationException type and message.
        Assert.Contains("InvalidOperationException", json);
        Assert.Contains("Consumer event handler not set", json);
    }
}
