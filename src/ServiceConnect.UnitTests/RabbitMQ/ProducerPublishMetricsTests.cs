using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Diagnostics;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.UnitTests.Diagnostics;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Drives <see cref="Producer.PublishAsync"/> against a mocked broker and asserts the
/// OTel-standard metrics fire on success and on failure — duration always, the
/// <c>messaging.client.published.messages</c> counter only on success.
/// </summary>
public sealed class ProducerPublishMetricsTests
{
    [Fact]
    public async Task PublishAsync_OnSuccess_RecordsPublishDurationAndIncrementsPublishedMessages()
    {
        // PublishAsync(Type) sets messaging.destination.name to the per-type exchange name.
        // Use a per-test message type so the exchange-name filter isolates emissions from
        // any other test running in parallel.
        var exchangeName = ServiceConnect.Services.MessageTypeExchangeName.From(typeof(SuccessMessage));
        using var collector = new MetricCollector("messaging.destination.name", exchangeName);
        await using var producer = BuildProducerWithMockChannel(out _, basicPublishThrows: false);

        await producer.PublishAsync(typeof(SuccessMessage), new byte[] { 1, 2, 3 });

        var durationRecords = collector.GetDoubleRecords(MetricNames.PublishDuration);
        var publishedRecords = collector.GetLongRecords(MetricNames.PublishedMessages);

        var duration = Assert.Single(durationRecords);
        Assert.Equal("rabbitmq", duration.GetTag("messaging.system"));
        Assert.Equal("publish", duration.GetTag("messaging.operation.type"));
        Assert.Equal("publish", duration.GetTag("messaging.operation.name"));
        Assert.Null(duration.GetTag("messaging.operation"));
        // PublishAsync(Type) destination is the per-type exchange name; non-empty proves
        // the destination was resolved before the metric emit.
        Assert.False(string.IsNullOrEmpty(duration.GetTag("messaging.destination.name")));
        Assert.Null(duration.GetTag("error.type"));
        Assert.True(duration.Value >= 0);

        var published = Assert.Single(publishedRecords);
        Assert.Equal(1, published.Value);
        Assert.Equal("rabbitmq", published.GetTag("messaging.system"));
        Assert.Equal("publish", published.GetTag("messaging.operation.type"));
        Assert.Equal("publish", published.GetTag("messaging.operation.name"));
        Assert.Null(published.GetTag("messaging.operation"));
        Assert.Equal(duration.GetTag("messaging.destination.name"), published.GetTag("messaging.destination.name"));
    }

    [Fact]
    public async Task PublishAsync_OnFailure_DoesNotIncrementPublishedMessages()
    {
        var exchangeName = ServiceConnect.Services.MessageTypeExchangeName.From(typeof(FailureMessage));
        using var collector = new MetricCollector("messaging.destination.name", exchangeName);
        await using var producer = BuildProducerWithMockChannel(out _, basicPublishThrows: true);

        // PublishAsync surfaces broker-side failures as exceptions; the metric must still fire.
        await Assert.ThrowsAnyAsync<Exception>(() => producer.PublishAsync(typeof(FailureMessage), new byte[] { 1, 2, 3 }));

        var durationRecords = collector.GetDoubleRecords(MetricNames.PublishDuration);
        var publishedRecords = collector.GetLongRecords(MetricNames.PublishedMessages);

        var duration = Assert.Single(durationRecords);
        Assert.Equal("rabbitmq", duration.GetTag("messaging.system"));
        Assert.Equal("publish", duration.GetTag("messaging.operation.type"));
        Assert.Equal("publish", duration.GetTag("messaging.operation.name"));
        Assert.Null(duration.GetTag("messaging.operation"));
        // The destination resolved before the publish call — failure happens at BasicPublishAsync,
        // by which point GetExchangeName has already populated the exchange name.
        Assert.False(string.IsNullOrEmpty(duration.GetTag("messaging.destination.name")));
        // error.type must be populated on the failure path.
        Assert.NotNull(duration.GetTag("error.type"));

        // Counter does NOT increment on failure — the published-messages counter is success-only.
        Assert.Empty(publishedRecords);
    }

    private static Producer BuildProducerWithMockChannel(out Mock<IChannel> channel, bool basicPublishThrows)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");

        // Tight retry budget so a failing publish surfaces quickly without burning the
        // default 60 × 10s reconnect window inside the test.
        var settings = new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.RetryCount] = (ushort)0,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)0,
        };
        transport.SetupGet(t => t.ClientSettings).Returns(settings);

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("publish-metrics-q");

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        var mockChannel = new Mock<IChannel>();
        mockChannel.SetupGet(c => c.IsOpen).Returns(true);
        mockChannel
            .Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>>(), false, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        if (basicPublishThrows)
        {
            mockChannel
                .Setup(c => c.BasicPublishAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                    It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<CancellationToken>()))
                // InvalidOperationException IS retriable per IsRetriablePublishException, but
                // RetryCount=0 (set on the transport above) makes the publish surface on the
                // first attempt regardless. Used in place of TimeoutException because TimeoutException
                // is now treated as confirm-timeout-indeterminate by EmitPublishMetrics — the
                // duration metric suppresses error.type for that case, breaking this test's
                // "error.type populated on failure" assertion. InvalidOperationException keeps
                // the assertion meaningful.
                .ThrowsAsync(new InvalidOperationException("publish failed in test"));
        }
        else
        {
            mockChannel
                .Setup(c => c.BasicPublishAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                    It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<CancellationToken>()))
                .Returns(ValueTask.CompletedTask);
        }

        var producer = new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);

        var fakeConnection = new Mock<IConnection>();
        fakeConnection.SetupGet(c => c.IsOpen).Returns(true);
        fakeConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockChannel.Object);
        producer.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(fakeConnection.Object);

        channel = mockChannel;
        return producer;
    }

    private sealed class SuccessMessage { }
    private sealed class FailureMessage { }
}
