using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public class ProducerRetryJitterTests
{
    // Use a non-zero mean so jitter produces observable spread in [mean*0.5, mean*1.5).
    private const ushort RetryCount = 7;
    private const ushort RetrySeconds = 10;

    private static Producer CreateProducer()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.RetryCount] = RetryCount,
            [RabbitMQSettingKeys.RetrySeconds] = RetrySeconds,
        });

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        return new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);
    }

    private static void StubLifecycleSurface(Mock<IConnection> connection)
    {
        connection.SetupGet(c => c.Endpoint).Returns(new AmqpTcpEndpoint("localhost", 5672));
        connection.SetupGet(c => c.ClientProvidedName).Returns("test");
    }

    private static T GetField<T>(Producer producer, string fieldName) =>
        ProducerInternals.GetField<T>(producer, fieldName);

    private static void SetField<T>(Producer producer, string fieldName, T value) =>
        ProducerInternals.SetField(producer, fieldName, value);

    [Fact]
    public async Task ChannelTransientFailures_UseJitteredDelaysAroundMean()
    {
        // Drive ChannelTransientException retries by having ExchangeDeclareAsync throw
        // ChannelTransientException on every attempt. ChannelTransientException is caught by
        // ExecuteRetryingPublishAsync without calling MarkResetRequired — the transient path skips
        // the reconnect budget and retries via EnsureConnectedAsync's fast path.
        //
        // Assertions mirror the IsRetriablePublishException test:
        //   1. Exactly RetryCount delays are observed.
        //   2. Every delay falls in [mean*0.5, mean*1.5).
        //   3. At least 2 distinct delays appear across the RetryCount samples.
        var producer = CreateProducer();

        var capturedDelays = new ConcurrentBag<TimeSpan>();
        producer.RetryDelayForTests = (delay, _) =>
        {
            capturedDelays.Add(delay);
            return Task.CompletedTask;
        };

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ChannelTransientException("simulated transient channel tear-down"));

        // Set _model and _connected so EnsureConnectedAsync fast-paths on every attempt —
        // ChannelTransientException does not call MarkResetRequired, so no reconnect runs.
        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        // Do NOT stamp _declaredExchanges: EnsureExchangeDeclaredAsync must run so that
        // ExchangeDeclareAsync is called and throws ChannelTransientException.

        // After RetryCount retries the exception propagates on the final attempt.
        await Assert.ThrowsAsync<ChannelTransientException>(() =>
            producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }));

        var delays = capturedDelays.ToArray();
        Assert.Equal(RetryCount, delays.Length);

        var meanSeconds = (double)RetrySeconds;
        var low = TimeSpan.FromSeconds(meanSeconds * 0.5);
        var high = TimeSpan.FromSeconds(meanSeconds * 1.5);
        foreach (var d in delays)
        {
            Assert.InRange(d, low, high);
        }

        // Jitter must produce at least 2 distinct values across RetryCount=7 samples.
        var distinctCount = delays.Distinct().Count();
        Assert.True(distinctCount >= 2,
            $"Expected at least 2 distinct retry delays; got {distinctCount} distinct value(s) from {delays.Length} samples. " +
            "All delays identical indicates no jitter is applied.");
    }

    [Fact]
    public async Task RetrieablePublishFailures_UseJitteredDelaysAroundMean()
    {
        // Drive BasicPublishAsync failures (classified as retriable by IsRetriablePublishException)
        // across all RetryCount retry slots. The RetryDelayForTests seam intercepts each
        // computed delay so the exact durations can be inspected.
        //
        // Assertions:
        //   1. Exactly RetryCount delays are observed (one per retry, final attempt does not delay).
        //   2. Every delay falls in [mean*0.5, mean*1.5) — JitteredRetryDelay's contract.
        //   3. At least 2 distinct delays appear across the RetryCount samples. With a fixed
        //      delay all samples are identical; with ±50% uniform jitter the probability that
        //      7 independent samples resolve to the same nanosecond is negligible (<1e-90).
        var producer = CreateProducer();

        var capturedDelays = new ConcurrentBag<TimeSpan>();
        producer.RetryDelayForTests = (delay, _) =>
        {
            capturedDelays.Add(delay);
            return Task.CompletedTask;
        };

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated publish failure"));

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        // Stamp the declared-exchange cache so the first attempt skips ExchangeDeclareAsync.
        // Subsequent attempts reconnect (MarkResetRequired clears the cache) and re-declare.
        var declaredExchanges = GetField<ConcurrentDictionary<string, long>>(producer, "_declaredExchanges");
        declaredExchanges["SystemObject"] = 0L;

        // CreateConnectionForTests wires reconnect attempts (MarkResetRequired triggers
        // EnsureConnectedAsync to rebuild the channel on every retry after the first attempt).
        var connection = new Mock<IConnection>();
        StubLifecycleSurface(connection);
        connection.SetupGet(c => c.IsOpen).Returns(true);
        connection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel.Object);
        producer.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(connection.Object);

        // After RetryCount retries (delays fired on attempts 0 through RetryCount-1)
        // the exception propagates on attempt RetryCount.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }));

        var delays = capturedDelays.ToArray();
        Assert.Equal(RetryCount, delays.Length);

        var meanSeconds = (double)RetrySeconds;
        var low = TimeSpan.FromSeconds(meanSeconds * 0.5);
        var high = TimeSpan.FromSeconds(meanSeconds * 1.5);
        foreach (var d in delays)
        {
            Assert.InRange(d, low, high);
        }

        // Jitter must produce at least 2 distinct values across RetryCount=7 samples.
        var distinctCount = delays.Distinct().Count();
        Assert.True(distinctCount >= 2,
            $"Expected at least 2 distinct retry delays; got {distinctCount} distinct value(s) from {delays.Length} samples. " +
            "All delays identical indicates no jitter is applied.");
    }
}
