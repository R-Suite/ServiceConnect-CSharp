using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class MessageRetryHandlerCopyPropsTests
{
    [Fact]
    public async Task HandleFailureAsync_RetryPublish_PreservesAllSourceProperties()
    {
        BasicProperties? capturedProps = null;
        var channel = new Mock<IChannel>();
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (_, _, _, p, _, _) => capturedProps = p)
            .Returns(ValueTask.CompletedTask);

        var sourceProps = new BasicProperties
        {
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            DeliveryMode = DeliveryModes.Persistent,
            Priority = (byte)5,
            CorrelationId = "corr-1",
            ReplyTo = "reply.queue",
            Expiration = "60000",
            MessageId = "msg-1",
            Timestamp = new AmqpTimestamp(1234567890),
            Type = "MyMessage",
            UserId = "guest",
            AppId = "test-app",
            ClusterId = "cluster-1",
        };

        var args = new BasicDeliverEventArgs(
            consumerTag: "ct",
            deliveryTag: 1,
            redelivered: false,
            exchange: "",
            routingKey: "main",
            properties: sourceProps,
            body: new byte[] { 1 });

        var handler = new MessageRetryHandler(maxRetries: 3, errorExchange: "error", NullLogger.Instance);
        var headers = new Dictionary<string, object>(StringComparer.Ordinal);

        await handler.HandleFailureAsync(channel.Object, "main.Retries", args, headers, ex: null);

        Assert.NotNull(capturedProps);
        Assert.Equal("application/json", capturedProps.ContentType);
        Assert.Equal("utf-8", capturedProps.ContentEncoding);
        Assert.Equal(DeliveryModes.Persistent, capturedProps.DeliveryMode);
        Assert.Equal((byte)5, capturedProps.Priority);
        Assert.Equal("corr-1", capturedProps.CorrelationId);
        Assert.Equal("reply.queue", capturedProps.ReplyTo);
        Assert.Equal("60000", capturedProps.Expiration);
        Assert.Equal("msg-1", capturedProps.MessageId);
        Assert.Equal(new AmqpTimestamp(1234567890), capturedProps.Timestamp);
        Assert.Equal("MyMessage", capturedProps.Type);
        Assert.Equal("guest", capturedProps.UserId);
        Assert.Equal("test-app", capturedProps.AppId);
        Assert.Equal("cluster-1", capturedProps.ClusterId);
    }

    [Fact]
    public async Task HandleTerminalFailureAsync_PreservesAllSourceProperties()
    {
        BasicProperties? capturedProps = null;
        var channel = new Mock<IChannel>();
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (_, _, _, p, _, _) => capturedProps = p)
            .Returns(ValueTask.CompletedTask);

        var sourceProps = new BasicProperties
        {
            ContentType = "application/json",
            CorrelationId = "corr-2",
            MessageId = "msg-2",
            Type = "TerminalMsg",
        };

        var args = new BasicDeliverEventArgs(
            consumerTag: "ct",
            deliveryTag: 1,
            redelivered: false,
            exchange: "",
            routingKey: "main",
            properties: sourceProps,
            body: new byte[] { 1 });

        var handler = new MessageRetryHandler(maxRetries: 3, errorExchange: "error", NullLogger.Instance);
        var headers = new Dictionary<string, object>(StringComparer.Ordinal);

        await handler.HandleTerminalFailureAsync(channel.Object, args, headers, new InvalidOperationException("test"));

        Assert.NotNull(capturedProps);
        Assert.Equal("application/json", capturedProps.ContentType);
        Assert.Equal("corr-2", capturedProps.CorrelationId);
        Assert.Equal("msg-2", capturedProps.MessageId);
        Assert.Equal("TerminalMsg", capturedProps.Type);
    }
}
