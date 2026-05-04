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
/// Asserts the <see cref="MetricNames.PublishConfirmTimeouts"/> counter increments at the
/// <see cref="TimeoutException"/> remap site in <c>Producer.PublishWithTimeoutAsync</c>.
/// Uses the same hanging-channel pattern as <see cref="ProducerPublishTimeoutTimingTests"/>
/// but with a much shorter timeout (100ms) so the test completes in well under a second.
/// </summary>
/// <remarks>
/// Serial collection because (a) the SendAsync test filters the global meter on the literal
/// <c>"&lt;empty&gt;"</c> destination sentinel — a future test class that emits the same
/// counter with an empty exchange would poison Assert.Single under parallel xUnit scheduling —
/// and (b) the 100ms timeout window is itself sensitive to thread-pool contention from
/// parallel tests. Aligns with the project's existing timing-sensitive collection.
/// </remarks>
[Collection(SerialConcurrencyCollection.Name)]
public sealed class ProducerConfirmTimeoutMetricsTests
{
    [Fact]
    public async Task PublishAsync_WhenBasicPublishHangs_RemapTimeoutEmitsConfirmTimeoutCounter()
    {
        // Per-test message type → unique exchange name so the MetricCollector tag-filter
        // isolates this test's emissions from any others running in parallel.
        var exchangeName = ServiceConnect.Services.MessageTypeExchangeName.From(typeof(ConfirmTimeoutProbeMessage));
        using var collector = new MetricCollector("messaging.destination.name", exchangeName);

        await using var producer = BuildProducerWithHangingChannel();

        // The hanging channel guarantees BasicPublishAsync never resolves; PublishWithTimeoutAsync's
        // linked CTS fires after 100ms and the OperationCanceledException is remapped to TimeoutException.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.PublishAsync(typeof(ConfirmTimeoutProbeMessage), new byte[] { 1, 2, 3 }));

        var record = Assert.Single(collector.GetLongRecords(MetricNames.PublishConfirmTimeouts));
        Assert.Equal(1, record.Value);
        Assert.Equal("rabbitmq", record.GetTag("messaging.system"));
        Assert.Equal(exchangeName, record.GetTag("messaging.destination.name"));
    }

    [Fact]
    public async Task SendAsync_WhenBasicPublishHangs_RemapTimeoutEmitsConfirmTimeoutCounterWithEmptyDestination()
    {
        // SendAsync passes exchange="" and routes via the queue routing key, so the metric's
        // messaging.destination.name carries the conventional "<empty>" sentinel.
        using var collector = new MetricCollector("messaging.destination.name", "<empty>");

        await using var producer = BuildProducerWithHangingChannel();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.SendAsync("send-confirm-timeout-q", typeof(ConfirmTimeoutProbeMessage), new byte[] { 1, 2, 3 }));

        var record = Assert.Single(collector.GetLongRecords(MetricNames.PublishConfirmTimeouts));
        Assert.Equal(1, record.Value);
        Assert.Equal("rabbitmq", record.GetTag("messaging.system"));
        Assert.Equal("<empty>", record.GetTag("messaging.destination.name"));
    }

    private static Producer BuildProducerWithHangingChannel()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");

        // Short publish timeout so the test resolves quickly. Tight retry budget so a remapped
        // TimeoutException surfaces immediately (not retriable per IsRetriablePublishException).
        var settings = new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.PublishTimeout] = TimeSpan.FromMilliseconds(100),
            [RabbitMQSettingKeys.RetryCount] = (ushort)0,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)0,
        };
        transport.SetupGet(t => t.ClientSettings).Returns(settings);

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("confirm-timeout-q");

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        // BasicPublishAsync hangs until the caller's CT (the linked timeout CTS) fires —
        // deterministic timeout trigger without depending on broker behaviour.
        var hangingChannel = new Mock<IChannel>();
        hangingChannel.SetupGet(c => c.IsOpen).Returns(true);
        hangingChannel
            .Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>>(), false, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        hangingChannel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, string _, bool _, BasicProperties _, ReadOnlyMemory<byte> _, CancellationToken ct) =>
                new ValueTask(Task.Delay(Timeout.Infinite, ct)));

        var producer = new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);

        var fakeConnection = new Mock<IConnection>();
        fakeConnection.SetupGet(c => c.IsOpen).Returns(true);
        fakeConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(hangingChannel.Object);
        producer.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(fakeConnection.Object);
        // The producer marks the connection for reset on timeout; the test must not block on
        // the seam reconnect path, so resolve it instantly.
        producer.ReconnectForTests = _ => Task.CompletedTask;

        return producer;
    }

    private sealed class ConfirmTimeoutProbeMessage { }
}
