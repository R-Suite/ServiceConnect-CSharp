using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Diagnostics;
using ServiceConnect.UnitTests.Diagnostics;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Asserts the <see cref="MetricNames.RetryAttempts"/> counter increments at the retry-counter
/// header bump in <c>MessageRetryHandler.HandleFailureAsync</c>. The metric fires before the
/// downstream BasicPublishAsync; the publish itself may still fail and surface as
/// <see cref="MetricNames.RetryDrops"/> via <c>InboundMessageProcessor</c>.
/// </summary>
public sealed class MessageRetryHandlerMetricsTests
{
    private static BasicDeliverEventArgs MakeArgs() => new(
        consumerTag: "ct",
        deliveryTag: 1,
        redelivered: false,
        exchange: "",
        routingKey: "main",
        properties: new BasicProperties(),
        body: new byte[] { 1 });

    [Fact]
    public async Task HandleFailureAsync_RetryPath_IncrementsRetryAttempts()
    {
        // Per-test unique queue name so MetricCollector's tag filter isolates this test
        // from any other test running in parallel that emits on the same instrument.
        var retryQueueName = $"q-retry-{Guid.NewGuid():N}.Retries";
        using var collector = new MetricCollector("messaging.destination.name", retryQueueName);

        var channel = new Mock<IChannel>();
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var handler = new MessageRetryHandler(maxRetries: 3, errorExchange: "err", NullLogger.Instance);
        var headers = new Dictionary<string, object>(StringComparer.Ordinal);

        await handler.HandleFailureAsync(channel.Object, retryQueueName, MakeArgs(), headers, ex: null);

        var record = Assert.Single(collector.GetLongRecords(MetricNames.RetryAttempts));
        Assert.Equal(1, record.Value);
        Assert.Equal("rabbitmq", record.GetTag("messaging.system"));
        Assert.Equal(retryQueueName, record.GetTag("messaging.destination.name"));
    }

    [Fact]
    public async Task HandleFailureAsync_MaxRetriesExceeded_DoesNotIncrementRetryAttempts()
    {
        // maxRetries=0 routes the first failure straight to the error exchange (no retry-publish),
        // so the increment site is not reached and the counter stays at zero.
        var retryQueueName = $"q-noretry-{Guid.NewGuid():N}.Retries";
        using var collector = new MetricCollector("messaging.destination.name", retryQueueName);

        var channel = new Mock<IChannel>();
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        var handler = new MessageRetryHandler(maxRetries: 0, errorExchange: "err", NullLogger.Instance);
        var headers = new Dictionary<string, object>(StringComparer.Ordinal);

        await handler.HandleFailureAsync(channel.Object, retryQueueName, MakeArgs(), headers, ex: new InvalidOperationException("test"));

        Assert.Empty(collector.GetLongRecords(MetricNames.RetryAttempts));
    }
}
