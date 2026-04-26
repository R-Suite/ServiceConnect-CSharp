using System.Collections.Concurrent;
using System.Reflection;
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

    private static void SetField<T>(Producer producer, string fieldName, T value)
    {
        typeof(Producer)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(producer, value);
    }

    private static T GetField<T>(Producer producer, string fieldName)
    {
        return (T)typeof(Producer)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(producer)!;
    }

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
        var declaredExchanges = GetField<ConcurrentDictionary<string, bool>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = true;

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);
        // Prevent the retry loop from triggering a real reconnect.
        producer.ReconnectForTests = _ => Task.CompletedTask;

        // Act & Assert
        await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.PublishAsync(typeof(object), [1, 2, 3]));
    }

    [Fact]
    public async Task SendAsync_ByType_ThrowsTimeoutException_WhenBasicPublishHangs()
    {
        var producer = CreateProducer(publishTimeout: TimeSpan.FromMilliseconds(100));
        var channel = MakeHangingChannel();

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);
        producer.ReconnectForTests = _ => Task.CompletedTask;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.SendAsync(typeof(object), [1, 2, 3]));
    }

    [Fact]
    public async Task SendAsync_ByEndpoint_ThrowsTimeoutException_WhenBasicPublishHangs()
    {
        var producer = CreateProducer(publishTimeout: TimeSpan.FromMilliseconds(100));
        var channel = MakeHangingChannel();

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);
        producer.ReconnectForTests = _ => Task.CompletedTask;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.SendAsync("destination-queue", typeof(object), [1, 2, 3]));
    }

    [Fact]
    public async Task SendBytesAsync_ThrowsTimeoutException_WhenBasicPublishHangs()
    {
        var producer = CreateProducer(publishTimeout: TimeSpan.FromMilliseconds(100));
        var channel = MakeHangingChannel();

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);
        producer.ReconnectForTests = _ => Task.CompletedTask;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.SendBytesAsync("destination-queue", typeof(object), [1, 2, 3]));
    }

    [Fact]
    public async Task PublishAsync_DoesNotThrowTimeoutException_WhenBrokerAcksPromptly()
    {
        // Arrange: short timeout, but the channel acks immediately — no timeout should fire.
        var producer = CreateProducer(publishTimeout: TimeSpan.FromMilliseconds(100));
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        var declaredExchanges = GetField<ConcurrentDictionary<string, bool>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = true;

        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        // Act: should complete without exception
        await producer.PublishAsync(typeof(object), [1, 2, 3]);
    }

    [Fact]
    public async Task PublishAsync_PropagatesOperationCanceledException_WhenCallerCancels()
    {
        // The caller's cancellation should propagate as OperationCanceledException,
        // NOT be swallowed and converted to TimeoutException.
        var producer = CreateProducer(publishTimeout: TimeSpan.FromSeconds(30));
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        var declaredExchanges = GetField<ConcurrentDictionary<string, bool>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = true;

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
        producer.ReconnectForTests = _ => Task.CompletedTask;

        // Should be OperationCanceledException (or a subclass such as TaskCanceledException), not TimeoutException
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            producer.PublishAsync(typeof(object), [1, 2, 3], cancellationToken: cts.Token));
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
    public async Task PublishAsync_ResetsConnection_WhenBasicPublishTimesOut()
    {
        // Arrange: short timeout so the test doesn't wait long.
        // Verify that ReconnectForTests (the test-seam for ReconnectAfterPublishFailureAsync)
        // is invoked after the publish times out, clearing the stale confirm-tracker state.
        var producer = CreateProducer(publishTimeout: TimeSpan.FromMilliseconds(100));
        var channel = MakeHangingChannel();
        var declaredExchanges = GetField<ConcurrentDictionary<string, bool>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = true;

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        var reconnectCalled = false;
        producer.ReconnectForTests = _ =>
        {
            reconnectCalled = true;
            return Task.CompletedTask;
        };

        // Act
        await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.PublishAsync(typeof(object), [1, 2, 3]));

        // Assert: the reconnect hook must have been invoked to clear the confirm-tracker
        Assert.True(reconnectCalled, "Expected the connection reset to be called after publish timeout.");
    }

    [Fact]
    public async Task PublishAsync_TimeoutException_ContainsExchangeAndRoutingKeyDetails()
    {
        // Verify that the enriched TimeoutException message includes exchange / routingKey /
        // messageId so operators have enough context for post-mortem correlation.
        var producer = CreateProducer(publishTimeout: TimeSpan.FromMilliseconds(100));
        var channel = MakeHangingChannel();
        var declaredExchanges = GetField<ConcurrentDictionary<string, bool>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = true;

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);
        producer.ReconnectForTests = _ => Task.CompletedTask;

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.PublishAsync(typeof(object), [1, 2, 3]));

        // The message must contain the contextual fields added by I2.
        Assert.Contains("exchange=", ex.Message);
        Assert.Contains("routingKey=", ex.Message);
        Assert.Contains("messageId=", ex.Message);
    }
}
