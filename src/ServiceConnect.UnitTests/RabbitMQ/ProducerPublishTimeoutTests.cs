using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Verifies that <see cref="Producer"/> enforces a publish-side timeout under publisher confirms.
/// A half-open connection or broker stall would otherwise hold <c>_publishLock</c> indefinitely.
/// </summary>
public class ProducerPublishTimeoutTests
{
    private static Producer CreateProducer(TimeSpan? publishTimeout = null)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");

        var settings = new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.RetryCount] = (ushort)1,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)0,
        };
        if (publishTimeout.HasValue)
        {
            settings[RabbitMQSettingKeys.PublishTimeout] = publishTimeout.Value;
        }

        transport.SetupGet(t => t.ClientSettings).Returns(settings);

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");
        queue.Setup(q => q.TryGetQueueMapping(It.IsAny<Type>(), out It.Ref<IReadOnlyList<string>?>.IsAny))
            .Returns((Type _, out IReadOnlyList<string>? endpoints) =>
            {
                endpoints = ["q"];
                return true;
            });

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        return new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);
    }

    private static void SetField<T>(Producer producer, string fieldName, T value) =>
        ProducerInternals.SetField(producer, fieldName, value);

    private static T GetField<T>(Producer producer, string fieldName) =>
        ProducerInternals.GetField<T>(producer, fieldName);

    /// <summary>
    /// Helper: set up a channel whose BasicPublishAsync hangs until its CancellationToken fires.
    /// This simulates a half-open connection or broker stall under publisher confirms.
    /// </summary>
    private static Mock<IChannel> MakeHangingChannel()
    {
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, string _, bool _, BasicProperties _, ReadOnlyMemory<byte> _, CancellationToken ct) =>
            {
                // Simulate an indefinitely-stalled broker: Task.Delay(Infinite) resolves only
                // when the supplied token is cancelled — i.e., when our linked timeout CTS fires.
                return new ValueTask(Task.Delay(Timeout.Infinite, ct));
            });
        return channel;
    }

    [Fact]
    public async Task PublishAsync_ThrowsTimeoutException_WhenBasicPublishHangs()
    {
        // Arrange: short timeout so the test doesn't wait long
        var producer = CreateProducer(publishTimeout: TimeSpan.FromMilliseconds(100));
        var channel = MakeHangingChannel();
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        // Act & Assert
        await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public async Task SendAsync_ByType_ThrowsAggregateException_ContainingTimeoutException_WhenBasicPublishHangs()
    {
        // B.2 (fan-out continue-on-failure): SendAsync(Type) now collects per-endpoint failures
        // and surfaces them as AggregateException. A single-endpoint mapping still wraps the
        // TimeoutException — callers must unwrap or use .Handle()/.Flatten().
        var producer = CreateProducer(publishTimeout: TimeSpan.FromMilliseconds(100));
        var channel = MakeHangingChannel();

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        var ex = await Assert.ThrowsAsync<AggregateException>(() =>
            producer.SendAsync(typeof(object), new byte[] { 1, 2, 3 }));

        Assert.Single(ex.InnerExceptions);
        Assert.IsType<TimeoutException>(ex.InnerExceptions[0]);
    }

    [Fact]
    public async Task SendAsync_ByEndpoint_ThrowsTimeoutException_WhenBasicPublishHangs()
    {
        var producer = CreateProducer(publishTimeout: TimeSpan.FromMilliseconds(100));
        var channel = MakeHangingChannel();

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.SendAsync("destination-queue", typeof(object), new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public async Task SendBytesAsync_ThrowsTimeoutException_WhenBasicPublishHangs()
    {
        var producer = CreateProducer(publishTimeout: TimeSpan.FromMilliseconds(100));
        var channel = MakeHangingChannel();

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.SendBytesAsync("destination-queue", typeof(object), new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public async Task PublishAsync_DoesNotThrowTimeoutException_WhenBrokerAcksPromptly()
    {
        // Arrange: short timeout, but the channel acks immediately — no timeout should fire.
        var producer = CreateProducer(publishTimeout: TimeSpan.FromMilliseconds(100));
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;

        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        // Act: should complete without exception
        await producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task PublishAsync_PropagatesOperationCanceledException_WhenCallerCancels()
    {
        // The caller's cancellation should propagate as OperationCanceledException,
        // NOT be swallowed and converted to TimeoutException.
        var producer = CreateProducer(publishTimeout: TimeSpan.FromSeconds(30));
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;

        using var cts = new CancellationTokenSource();

        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, string _, bool _, BasicProperties _, ReadOnlyMemory<byte> _, CancellationToken ct) =>
            {
                // Cancel the caller's token, simulating bus shutdown during publish.
                cts.Cancel();
                return new ValueTask(Task.Delay(Timeout.Infinite, ct));
            });

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        // Should be OperationCanceledException (or a subclass such as TaskCanceledException), not TimeoutException
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }, cancellationToken: cts.Token));
        Assert.IsNotType<TimeoutException>(ex);
    }

    [Fact]
    public async Task PublishAsync_DefaultsToThirtySecondTimeout_WhenNotConfigured()
    {
        // Verifies the default is wired up: when no PublishTimeout key is set in ClientSettings,
        // _publishTimeout is 30 seconds. We check the field value rather than waiting 30s.
        var producer = CreateProducer(publishTimeout: null);

        var timeout = GetField<TimeSpan>(producer, "_publishTimeout");

        Assert.Equal(TimeSpan.FromSeconds(30), timeout);
    }

    [Fact]
    public async Task PublishAsync_MarksResetRequired_WhenBasicPublishTimesOut_WithoutReconnectingUnderLock()
    {
        // The publish-timeout catch path must defer the reconnect rather than driving it
        // inline. Driving it inline would hold _publishLock for up to retryCount *
        // retrySeconds — minutes — blocking concurrent publishers. Instead it sets the
        // reset-required flag on ProducerConnection; the next EnsureConnectedAsync (which
        // runs OUTSIDE _publishLock) consumes the flag under _connectionSemaphore.
        var producer = CreateProducer(publishTimeout: TimeSpan.FromMilliseconds(100));
        var channel = MakeHangingChannel();
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        // Act
        await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }));

        // Assert: the reset-required flag is set on _producerConnection so the next publish
        // drives the reset. Pre-restructure this used a ReconnectForTests counter to prove the
        // catch path didn't reconnect inline; that seam is now gone, but the deferred-reset
        // contract is observable via the reset flag (and the ProducerPublishTimeoutTimingTests
        // wall-clock test independently asserts the off-lock guarantee).
        var resetRequired = GetField<int>(producer, "_resetRequired");
        Assert.Equal(1, resetRequired);
    }

    [Fact]
    public async Task PublishAsync_TimeoutException_ContainsExchangeAndRoutingKeyDetails()
    {
        // Verify that the enriched TimeoutException message includes exchange / routingKey /
        // messageId so operators have enough context for post-mortem correlation.
        var producer = CreateProducer(publishTimeout: TimeSpan.FromMilliseconds(100));
        var channel = MakeHangingChannel();
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }));

        // The message must contain the contextual fields added by I2.
        Assert.Contains("exchange=", ex.Message);
        Assert.Contains("routingKey=", ex.Message);
        Assert.Contains("messageId=", ex.Message);
    }
}
