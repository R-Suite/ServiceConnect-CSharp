using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies the H22 fix: <see cref="Producer.PublishWithTimeoutAsync"/> must NOT await
/// <c>ReconnectAsync</c> while holding <c>_publishLock</c>. Doing so would block every
/// concurrent publisher for up to <c>retryCount * retrySeconds</c> (default 60 * 10s = 10 min).
/// Instead, the publish-timeout catch path sets a synchronous <c>_resetRequired</c> flag on
/// <see cref="ProducerConnection"/>; the next call to <c>EnsureConnectedAsync</c> consumes
/// the flag and drives the reconnect off the publish lock.
/// </summary>
public sealed class ProducerPublishTimeoutResetTests
{
    private static Producer CreateProducer(TimeSpan publishTimeout)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.PublishTimeout] = publishTimeout,
            [RabbitMQSettingKeys.RetryCount] = (ushort)1,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)1,
        });

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        return new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);
    }

    private static Mock<IChannel> MakeHangingChannel(TimeSpan delay)
    {
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, bool _, BasicProperties _, ReadOnlyMemory<byte> _, CancellationToken ct) =>
                new ValueTask(Task.Delay(delay, ct)));
        channel
            .Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>>(), false, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return channel;
    }

    private static Mock<IChannel> MakeImmediateChannel()
    {
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        channel
            .Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>>(), false, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return channel;
    }

    /// <summary>
    /// Pre-seed the producer with a known channel so the first publish skips the connection
    /// factory and goes straight to BasicPublishAsync. Mirrors the pattern in
    /// ProducerPublishTimeoutTests.
    /// </summary>
    private static void PrimeProducer(Producer producer, IChannel channel)
    {
        ProducerInternals.SetField(producer, "_model", channel);
        ProducerInternals.SetField(producer, "_connected", true);
        var declared = ProducerInternals.GetField<ConcurrentDictionary<string, bool>>(producer, "_declaredExchanges");
        declared["SystemObject"] = true;
    }

    private static int GetResetRequired(Producer producer) =>
        ProducerInternals.GetField<int>(producer, "_resetRequired");

    private static bool ResetRequiredFlag(Producer producer) => GetResetRequired(producer) == 1;

    [Fact]
    public async Task PublishTimeout_Throws_DoesNotReconnect_FlagsResetRequired()
    {
        // Arrange: 50ms publish timeout, 5s simulated publish hang.
        await using var producer = CreateProducer(TimeSpan.FromMilliseconds(50));
        var channel = MakeHangingChannel(TimeSpan.FromSeconds(5));
        PrimeProducer(producer, channel.Object);

        int reconnectCalls = 0;
        producer.ReconnectForTests = _ => { Interlocked.Increment(ref reconnectCalls); return Task.CompletedTask; };

        // Act: publish should time out.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }));

        // Assert: reconnect probe was NOT called from inside PublishWithTimeoutAsync's catch path.
        Assert.Equal(0, reconnectCalls);

        // Assert: reset-required flag is set on _producerConnection.
        Assert.True(ResetRequiredFlag(producer));
    }

    [Fact]
    public async Task NextPublishAfterTimeout_DrivesReset_BeforeAcquiringPublishLock()
    {
        // First channel hangs; second channel (built by the post-timeout reconnect) acks immediately.
        var hangingChannel = MakeHangingChannel(TimeSpan.FromSeconds(5));
        var freshChannel = MakeImmediateChannel();

        await using var producer = CreateProducer(TimeSpan.FromMilliseconds(50));
        PrimeProducer(producer, hangingChannel.Object);

        // CreateConnectionForTests counts how many fresh connections are built. Each ReconnectAsync
        // on the no-test-hook path tears down then recurses into EnsureConnectedAsync, which calls
        // CreateConnectionAsync exactly once. So this counter == reconnect-driven rebuilds.
        int connectionBuilds = 0;
        var fakeConnection = new Mock<IConnection>();
        fakeConnection.SetupGet(c => c.IsOpen).Returns(true);
        fakeConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(freshChannel.Object);
        producer.CreateConnectionForTests = (_, _, _, _) =>
        {
            Interlocked.Increment(ref connectionBuilds);
            return Task.FromResult(fakeConnection.Object);
        };

        // Act: first publish times out → flag set, no reconnect yet.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 }));
        Assert.Equal(0, connectionBuilds);
        Assert.True(ResetRequiredFlag(producer));

        // Act: second publish drives the reset before its own publish runs.
        await producer.PublishAsync(typeof(object), new byte[] { 4, 5, 6 });

        // Assert: exactly one fresh connection was built — driven by the deferred reset on
        // EnsureConnectedAsync's flag-consume path, NOT by the first publish's catch.
        Assert.Equal(1, connectionBuilds);
        // Assert: flag was consumed.
        Assert.False(ResetRequiredFlag(producer));
    }

    [Fact]
    public async Task MarkResetRequired_IsIdempotent_ConcurrentTimeouts_OneReset()
    {
        // Arrange: two concurrent publishes both time out — both call MarkResetRequired,
        // but the flag is set-once, and the next publish drives EXACTLY ONE reconnect.
        var hangingChannel = MakeHangingChannel(TimeSpan.FromSeconds(5));
        var freshChannel = MakeImmediateChannel();

        await using var producer = CreateProducer(TimeSpan.FromMilliseconds(50));
        PrimeProducer(producer, hangingChannel.Object);

        int connectionBuilds = 0;
        var fakeConnection = new Mock<IConnection>();
        fakeConnection.SetupGet(c => c.IsOpen).Returns(true);
        fakeConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(freshChannel.Object);
        producer.CreateConnectionForTests = (_, _, _, _) =>
        {
            Interlocked.Increment(ref connectionBuilds);
            return Task.FromResult(fakeConnection.Object);
        };

        // Two concurrent timeouts. PublishAsync serialises on _publishLock, but the catch-path
        // MarkResetRequired call is non-blocking and idempotent regardless of order — both timeouts
        // set the flag to 1, but the flag is consumed exactly once on the next publish.
        var t1 = Assert.ThrowsAsync<TimeoutException>(() => producer.PublishAsync(typeof(object), new byte[] { 1 }));
        var t2 = Assert.ThrowsAsync<TimeoutException>(() => producer.PublishAsync(typeof(object), new byte[] { 2 }));
        await Task.WhenAll(t1, t2);

        Assert.Equal(0, connectionBuilds);
        Assert.True(ResetRequiredFlag(producer));

        // Subsequent fast publish should drive exactly ONE reset.
        await producer.PublishAsync(typeof(object), new byte[] { 3 });

        Assert.Equal(1, connectionBuilds);
        Assert.False(ResetRequiredFlag(producer));
    }
}
