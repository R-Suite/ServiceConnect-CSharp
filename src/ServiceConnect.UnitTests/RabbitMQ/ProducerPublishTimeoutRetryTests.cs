using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Client.RabbitMQ.Configuration;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Pins the producer's retry contract under the at-least-once delivery model: a publish-confirm
/// TimeoutException retries on a fresh attempt, the MessageId is preserved across attempts, and
/// MaxPublishWaitTime caps the total wall-clock budget so a permanently-dead broker cannot hold
/// a publisher indefinitely.
/// </summary>
public class ProducerPublishTimeoutRetryTests
{
    private static Producer CreateProducer(
        TimeSpan? publishTimeout = null,
        TimeSpan? maxPublishWaitTime = null,
        ushort retryCount = 2)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");

        var settings = new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.RetryCount] = retryCount,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)0,
        };
        if (publishTimeout.HasValue) { settings[RabbitMQSettingKeys.PublishTimeout] = publishTimeout.Value; }
        if (maxPublishWaitTime.HasValue) { settings[RabbitMQSettingKeys.MaxPublishWaitTime] = maxPublishWaitTime.Value; }

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

        var producer = new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance)
        {
            // Bypass Task.Delay in the inter-attempt path so retry tests are not wall-clock bound.
            RetryDelayForTests = (_, _) => Task.CompletedTask,
        };
        return producer;
    }

    // Returns a connection factory that produces channels whose BasicPublishAsync hangs until
    // the CancellationToken fires. Used by wall-clock cap tests to keep EnsureConnectedAsync
    // (post-timeout reconnect) fast — no real broker is running in unit tests.
    private static Func<ConnectionFactory, string[], string, CancellationToken, Task<IConnection>> MakeHangingConnectionFactory()
    {
        return (_, _, _, _) =>
        {
            var channel = new Mock<IChannel>();
            channel.SetupGet(c => c.IsOpen).Returns(true);
            channel.Setup(c => c.BasicPublishAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                    It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<CancellationToken>()))
                .Returns((string _, string _, bool _, BasicProperties _, ReadOnlyMemory<byte> _, CancellationToken ct) =>
                    new ValueTask(Task.Delay(Timeout.Infinite, ct)));

            var conn = new Mock<IConnection>();
            conn.SetupGet(c => c.IsOpen).Returns(true);
            conn.Setup(c => c.CreateChannelAsync(
                    It.IsAny<CreateChannelOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(channel.Object);
            return Task.FromResult(conn.Object);
        };
    }

    private static void SetField<T>(Producer producer, string fieldName, T value) =>
        ProducerInternals.SetField(producer, fieldName, value);

    private static T GetField<T>(Producer producer, string fieldName) =>
        ProducerInternals.GetField<T>(producer, fieldName);

    [Fact]
    public async Task PublishAsync_RetriesTimeoutException_SucceedsOnSecondAttempt()
    {
        var producer = CreateProducer(
            publishTimeout: TimeSpan.FromMilliseconds(100),
            maxPublishWaitTime: TimeSpan.FromSeconds(5));
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);

        var callCount = 0;
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, string _, bool _, BasicProperties _, ReadOnlyMemory<byte> _, CancellationToken ct) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Simulate the broker's confirm-ack never arriving so the
                    // publish-timeout CTS fires inside PublishWithTimeoutAsync.
                    return new ValueTask(Task.Delay(Timeout.Infinite, ct));
                }
                return ValueTask.CompletedTask;
            });

        // After the first attempt times out, EnsureConnectedAsync will try to reconnect.
        // Provide a connection factory that reuses the same hanging-then-succeeding channel
        // to avoid real broker connection attempts.
        producer.CreateConnectionForTests = (_, _, _, _) =>
        {
            var conn = new Mock<IConnection>();
            conn.SetupGet(c => c.IsOpen).Returns(true);
            conn.Setup(c => c.CreateChannelAsync(
                    It.IsAny<CreateChannelOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(channel.Object);
            return Task.FromResult(conn.Object);
        };

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        await producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 });

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task PublishAsync_RetriesTimeoutException_PreservesMessageIdAcrossAttempts()
    {
        var producer = CreateProducer(
            publishTimeout: TimeSpan.FromMilliseconds(100),
            maxPublishWaitTime: TimeSpan.FromSeconds(5));
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);

        var capturedMessageIds = new List<string?>();
        var callCount = 0;
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, string _, bool _, BasicProperties props, ReadOnlyMemory<byte> _, CancellationToken ct) =>
            {
                capturedMessageIds.Add(props.MessageId);
                callCount++;
                if (callCount == 1)
                {
                    return new ValueTask(Task.Delay(Timeout.Infinite, ct));
                }
                return ValueTask.CompletedTask;
            });

        producer.CreateConnectionForTests = (_, _, _, _) =>
        {
            var conn = new Mock<IConnection>();
            conn.SetupGet(c => c.IsOpen).Returns(true);
            conn.Setup(c => c.CreateChannelAsync(
                    It.IsAny<CreateChannelOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(channel.Object);
            return Task.FromResult(conn.Object);
        };

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        await producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 });

        Assert.Equal(2, capturedMessageIds.Count);
        Assert.NotNull(capturedMessageIds[0]);
        Assert.Equal(capturedMessageIds[0], capturedMessageIds[1]);
    }

    [Fact]
    public async Task PublishAsync_MaxPublishWaitTime_CapsRetryLoopWallClock()
    {
        var producer = CreateProducer(
            publishTimeout: TimeSpan.FromMilliseconds(100),
            maxPublishWaitTime: TimeSpan.FromMilliseconds(200),
            retryCount: 60);
        producer.CreateConnectionForTests = MakeHangingConnectionFactory();
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, string _, bool _, BasicProperties _, ReadOnlyMemory<byte> _, CancellationToken ct) =>
                new ValueTask(Task.Delay(Timeout.Infinite, ct)));

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }));
        sw.Stop();

        Assert.Contains("wall-clock budget", ex.Message, StringComparison.Ordinal);
        // RetryCount=60, PublishTimeout=100ms — without the cap the test would burn 6+ seconds.
        // The cap is 200ms; 1s allows ~800ms of CI overhead on top of the cap.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1),
            $"Test took {sw.Elapsed.TotalSeconds:F2}s but the cap was 200ms.");
    }

    [Fact]
    public async Task PublishAsync_MaxPublishWaitTime_InfiniteTimeSpan_DisablesCap()
    {
        var producer = CreateProducer(
            publishTimeout: TimeSpan.FromMilliseconds(50),
            maxPublishWaitTime: Timeout.InfiniteTimeSpan,
            retryCount: 2);
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);

        var callCount = 0;
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, string _, bool _, BasicProperties _, ReadOnlyMemory<byte> _, CancellationToken ct) =>
            {
                Interlocked.Increment(ref callCount);
                return new ValueTask(Task.Delay(Timeout.Infinite, ct));
            });

        // Share the same channel mock between the initial _model and the reconnect factory so
        // all 3 attempts (attempt 0 via _model, attempts 1 and 2 via factory) feed the same counter.
        producer.CreateConnectionForTests = (_, _, _, _) =>
        {
            var conn = new Mock<IConnection>();
            conn.SetupGet(c => c.IsOpen).Returns(true);
            conn.Setup(c => c.CreateChannelAsync(
                    It.IsAny<CreateChannelOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(channel.Object);
            return Task.FromResult(conn.Object);
        };

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }));
        Assert.DoesNotContain("wall-clock budget", ex.Message, StringComparison.Ordinal);
        // retryCount=2 means 3 total attempts (0, 1, 2). With InfiniteTimeSpan the cap is
        // disabled, so the loop runs all 3 before throwing. This complements the
        // CapsRetryLoopWallClock test by verifying the loop did NOT short-circuit.
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task PublishAsync_PublishException_StillPropagatesWithoutRetry()
    {
        var producer = CreateProducer(
            publishTimeout: TimeSpan.FromMilliseconds(100),
            maxPublishWaitTime: TimeSpan.FromSeconds(5),
            retryCount: 10);
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);

        var callCount = 0;
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, string _, bool _, BasicProperties _, ReadOnlyMemory<byte> _, CancellationToken _) =>
            {
                callCount++;
                throw new PublishException(1, false);
            });

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        await Assert.ThrowsAsync<PublishException>(() =>
            producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }));

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void RabbitMqOptions_Validate_RejectsZeroMaxPublishWaitTime()
    {
        var options = new RabbitMqOptions
        {
            MaxPublishWaitTime = TimeSpan.Zero,
        };

        var errors = options.Validate();

        Assert.Contains(errors, e => e.Contains("MaxPublishWaitTime", StringComparison.Ordinal));
    }
}
