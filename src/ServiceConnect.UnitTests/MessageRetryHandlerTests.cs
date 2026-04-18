using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MessageRetryHandlerTests
{
    private static BasicDeliverEventArgs MakeArgs(byte[]? body = null)
    {
        var props = new BasicProperties();
        return new BasicDeliverEventArgs(
            consumerTag: "tag",
            deliveryTag: 1,
            redelivered: false,
            exchange: "",
            routingKey: "q",
            properties: props,
            body: body ?? new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task HandleFailureAsync_UnderMaxRetries_IncrementsCountAndPublishesToRetryQueue()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var handler = new MessageRetryHandler(
            maxRetries: 3, errorExchange: "err", NullLogger.Instance);

        var args = MakeArgs();
        var headers = new Dictionary<string, object>();

        await handler.HandleFailureAsync(channel.Object, "q.Retries", args, headers, ex: null);

        Assert.Equal(1, (int)headers[HeaderKeys.RetryCount]);
        channel.Verify(c => c.BasicPublishAsync(
            string.Empty, "q.Retries", false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleFailureAsync_AtMaxRetries_PublishesToErrorExchange()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var handler = new MessageRetryHandler(maxRetries: 1, errorExchange: "err", NullLogger.Instance);
        var args = MakeArgs();
        var headers = new Dictionary<string, object> { [HeaderKeys.RetryCount] = 1 };

        await handler.HandleFailureAsync(channel.Object, "q.Retries", args, headers, ex: new InvalidOperationException("oops"));

        channel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleTerminalFailureAsync_PublishesToErrorExchange_WithoutIncrementingRetryCount()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var handler = new MessageRetryHandler(maxRetries: 3, errorExchange: "err", NullLogger.Instance);
        var args = MakeArgs();
        var headers = new Dictionary<string, object> { [HeaderKeys.RetryCount] = 2 };

        await handler.HandleTerminalFailureAsync(
            channel.Object,
            args,
            headers,
            new InvalidOperationException("invalid inbound message"));

        Assert.Equal(2, (int)headers[HeaderKeys.RetryCount]);
        channel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleTerminalFailureAsync_IncludesSanitizedExceptionPayload()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var handler = new MessageRetryHandler(maxRetries: 3, errorExchange: "err", NullLogger.Instance);
        var args = MakeArgs();
        var headers = new Dictionary<string, object>();

        await handler.HandleTerminalFailureAsync(
            channel.Object,
            args,
            headers,
            new InvalidOperationException("invalid inbound message"));

        var payload = JObject.Parse((string)headers[HeaderKeys.Exception]);
        Assert.Equal(typeof(InvalidOperationException).FullName, (string?)payload["ExceptionType"]);
        Assert.Contains("invalid inbound message", (string?)payload["Message"] ?? "");
        Assert.Null(payload["StackTrace"]);
    }

    [Fact]
    public async Task HandleFailureAsync_AtMaxRetries_IncludesExceptionTypeAndMessage_ButNoStackTrace()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var handler = new MessageRetryHandler(maxRetries: 0, errorExchange: "err", NullLogger.Instance);
        var args = MakeArgs();
        var headers = new Dictionary<string, object>();

        try { throw new InvalidOperationException("boom"); }
        catch (InvalidOperationException caught)
        {
            await handler.HandleFailureAsync(channel.Object, "q.Retries", args, headers, ex: caught);
        }

        Assert.True(headers.ContainsKey(HeaderKeys.Exception));
        var payload = JObject.Parse((string)headers[HeaderKeys.Exception]);
        Assert.Equal(typeof(InvalidOperationException).FullName, (string?)payload["ExceptionType"]);
        Assert.Contains("boom", (string?)payload["Message"] ?? "");
        Assert.Null(payload["StackTrace"]);
    }

    [Fact]
    public async Task HandleFailureAsync_NoException_StillPublishesToErrorExchange()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var handler = new MessageRetryHandler(maxRetries: 0, errorExchange: "err", NullLogger.Instance);
        var args = MakeArgs();
        var headers = new Dictionary<string, object>();

        await handler.HandleFailureAsync(channel.Object, "q.Retries", args, headers, ex: null);

        channel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(headers.ContainsKey(HeaderKeys.Exception));
    }

    [Fact]
    public async Task HandleFailureAsync_ReadsExistingRetryCount_FromHeaders()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var handler = new MessageRetryHandler(maxRetries: 5, errorExchange: "err", NullLogger.Instance);
        var args = MakeArgs();
        var headers = new Dictionary<string, object> { [HeaderKeys.RetryCount] = 2 };

        await handler.HandleFailureAsync(channel.Object, "q.Retries", args, headers, ex: null);

        Assert.Equal(3, (int)headers[HeaderKeys.RetryCount]);
    }

    [Fact]
    public async Task HandleFailureAsync_IgnoresCorruptRetryCount()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var handler = new MessageRetryHandler(maxRetries: 5, errorExchange: "err", NullLogger.Instance);
        var args = MakeArgs();
        var headers = new Dictionary<string, object> { [HeaderKeys.RetryCount] = "not-a-number" };

        await handler.HandleFailureAsync(channel.Object, "q.Retries", args, headers, ex: null);

        Assert.Equal(1, (int)headers[HeaderKeys.RetryCount]);
    }
}
