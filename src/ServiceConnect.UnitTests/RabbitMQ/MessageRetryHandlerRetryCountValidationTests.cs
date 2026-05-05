using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class MessageRetryHandlerRetryCountValidationTests
{
    private const int MaxRetries = 3;

    private static (MessageRetryHandler handler, Mock<IChannel> channel, List<(string Exchange, string RoutingKey)> publishes, List<(LogLevel Level, string Message)> logs) CreateHandler()
    {
        var publishes = new List<(string, string)>();
        var channel = new Mock<IChannel>();
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (ex, rk, _, _, _, _) => publishes.Add((ex, rk)))
            .Returns(ValueTask.CompletedTask);

        var captured = new List<(LogLevel, string)>();
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        logger.Setup(l => l.Log(
            It.IsAny<LogLevel>(),
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()))
            .Callback(new InvocationAction(invocation =>
            {
                var level = (LogLevel)invocation.Arguments[0];
                var formatter = (Delegate)invocation.Arguments[4];
                var message = (string)formatter.DynamicInvoke(invocation.Arguments[2], invocation.Arguments[3])!;
                captured.Add((level, message));
            }));

        var handler = new MessageRetryHandler(MaxRetries, "test.error", "test.consumer.queue", logger.Object);
        return (handler, channel, publishes, captured);
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
    public async Task RetryCount_EqualsMaxRetries_RoutesToErrorExchange_NotMalformed()
    {
        var (handler, channel, publishes, logs) = CreateHandler();
        var args = MakeArgs();
        var headers = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [HeaderKeys.RetryCount] = MaxRetries,
        };

        await handler.HandleFailureAsync(channel.Object, "main.Retries", args, headers, ex: null);

        // Routes to the error exchange (max-retries-exceeded path).
        var (exchange, _) = Assert.Single(publishes);
        Assert.Equal("test.error", exchange);

        // No "Malformed or out-of-range" warning — the value is at the legitimate boundary.
        Assert.DoesNotContain(logs, l => l.Message.Contains("Malformed or out-of-range"));
    }

    [Fact]
    public async Task RetryCount_GreaterThanMaxRetries_RoutedToErrorAsMalformed()
    {
        var (handler, channel, publishes, logs) = CreateHandler();
        var args = MakeArgs();
        var headers = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [HeaderKeys.RetryCount] = MaxRetries + 1,  // out-of-range
        };

        await handler.HandleFailureAsync(channel.Object, "main.Retries", args, headers, ex: null);

        // Routes to the error exchange via the "malformed" path.
        var (exchange, _) = Assert.Single(publishes);
        Assert.Equal("test.error", exchange);

        // The "Malformed or out-of-range" warning fires.
        var (_, message) = Assert.Single(logs, l => l.Level == LogLevel.Warning);
        Assert.Contains("Malformed or out-of-range", message);
    }
}
