using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies that Consumer.StartConsumingAsync rejects a ConsumerCount below 1 even when
/// the consumer is constructed directly, bypassing the builder validator.
/// </summary>
public sealed class ConsumerCountValidationTests
{
    private static Consumer CreateConsumerWithCount(int consumerCount)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.MaxRetries).Returns(0);
        transport.SetupGet(t => t.RetryDelay).Returns(0);
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");
        queue.SetupGet(q => q.ErrorQueueName).Returns("err");
        queue.SetupGet(q => q.AuditQueueName).Returns("audit");
        queue.SetupGet(q => q.PurgeQueueOnStartup).Returns(false);
        queue.SetupGet(q => q.AuditingEnabled).Returns(false);

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.ConsumerCount).Returns(consumerCount);

        // Provide a connection stub so StartConsumingAsync doesn't fail before the guard.
        var connection = new Mock<IServiceConnectConnection>();

        return new Consumer(transport.Object, queue.Object, bus.Object,
            NullLogger<Consumer>.Instance, connection.Object);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task StartConsumingAsync_ConsumerCountLessThanOne_ThrowsInvalidOperationException(int count)
    {
        var consumer = CreateConsumerWithCount(count);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            consumer.StartConsumingAsync(
                "q",
                ["TestMessage"],
                (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true })));

        Assert.Contains("BusConfiguration.ConsumerCount", ex.Message);
        Assert.Contains(count.ToString(), ex.Message);

        await consumer.DisposeAsync();
    }
}
