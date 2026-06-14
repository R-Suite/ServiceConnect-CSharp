using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class MessageRetryHandlerMandatoryTests
{
    private static (MessageRetryHandler handler, Mock<IChannel> channel, List<bool> mandatoryCaptures) CreateHandler(int maxRetries)
    {
        var captures = new List<bool>();
        var channel = new Mock<IChannel>();
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (_, _, mandatory, _, _, _) => captures.Add(mandatory))
            .Returns(ValueTask.CompletedTask);

        return (new MessageRetryHandler(maxRetries, "error.exchange", "test.consumer.queue", NullLogger.Instance), channel, captures);
    }

    private static BasicDeliverEventArgs MakeArgs() => new(
        consumerTag: "ct",
        deliveryTag: 1,
        redelivered: false,
        exchange: "",
        routingKey: "main",
        properties: new BasicProperties(),
        body: new byte[] { 1 });

    [Fact]
    public async Task HandleFailureAsync_RetryPath_PublishesMandatoryTrue()
    {
        var (handler, channel, captures) = CreateHandler(maxRetries: 3);
        var headers = new Dictionary<string, object>(StringComparer.Ordinal);

        await handler.HandleFailureAsync(channel.Object, "main.Retries", MakeArgs(), headers, ex: null);

        var mandatory = Assert.Single(captures);
        Assert.True(mandatory);
    }

    [Fact]
    public async Task HandleFailureAsync_MaxRetriesPath_PublishesMandatoryTrue()
    {
        var (handler, channel, captures) = CreateHandler(maxRetries: 0);  // first failure → error
        var headers = new Dictionary<string, object>(StringComparer.Ordinal);

        await handler.HandleFailureAsync(channel.Object, "main.Retries", MakeArgs(), headers, ex: null);

        var mandatory = Assert.Single(captures);
        Assert.True(mandatory);
    }

    [Fact]
    public async Task HandleTerminalFailureAsync_PublishesMandatoryTrue()
    {
        var (handler, channel, captures) = CreateHandler(maxRetries: 3);
        var headers = new Dictionary<string, object>(StringComparer.Ordinal);

        await handler.HandleTerminalFailureAsync(channel.Object, MakeArgs(), headers, new InvalidOperationException("test"));

        var mandatory = Assert.Single(captures);
        Assert.True(mandatory);
    }
}
