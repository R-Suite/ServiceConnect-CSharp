using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Asserts the user-visible wall-clock contract: a publish timeout returns to the caller
/// within ~publishTimeout, not within the reconnect retry budget. Uses Producer test seams to
/// bypass the broker so the timing window is deterministic. Complements
/// <see cref="ProducerPublishTimeoutResetTests"/> (which asserts the mechanism — flag set,
/// no in-catch reconnect) by asserting the wall-clock outcome.
/// </summary>
public sealed class ProducerPublishTimeoutTimingTests
{
    [Fact]
    public async Task ConcurrentPublishers_WhenPublishTimesOut_NoPublisherBlockedForReconnectBudget()
    {
        // Arrange ─────────────────────────────────────────────────────────────────────────────
        //   publishTimeout   = 200 ms  (how long BasicPublishAsync hangs before timeout fires)
        //   reconnectDelay   = 2 000 ms (simulated slow reconnect after the timeout)
        //   publisherCount   = 5
        //
        // Threshold = 3 000 ms. If EnsureConnectedAsync ran inside _publishLock the slow
        // reconnect would serialise behind every other publisher, pushing the worst publisher
        // above 7 s; with EnsureConnectedAsync outside the lock it stays near ~1 s.
        const int publisherCount = 5;
        const int publishTimeoutMs = 200;
        const int reconnectDelayMs = 2_000;
        var assertThreshold = TimeSpan.FromMilliseconds(3_000);

        var transport = new TransportConfiguration
        {
            // The mock channel replaces the broker connection, so no AMQP traffic flows
            // during the publish phase of this test.
            Host = "localhost",
            Username = "guest",
            Password = "guest",
            // EnsureConnectedAsync drives the post-reset recreate by calling
            // CreateConnectionAsync directly. CreateConnectionAsync calls
            // ConnectionFactoryBuilder.Build, which validates SSL config; the default
            // SslEnabled=true requires a ServerName the test doesn't supply. Plain-text
            // here matches the test's loopback Host.
            SslEnabled = false,
        };
        transport.SetClientSetting(RabbitMQSettingKeys.Port, 5672);
        transport.SetClientSetting(RabbitMQSettingKeys.PublishTimeout, TimeSpan.FromMilliseconds(publishTimeoutMs));
        // RetryCount / RetrySeconds bound how long EnsureConnectedAsync retries if
        // CreateConnectionAsync throws. Set to small non-default values so the test is
        // clearly scoped and CreateConnectionAsync failures cannot extend the wall clock.
        transport.SetClientSetting(RabbitMQSettingKeys.RetryCount, 1);
        transport.SetClientSetting(RabbitMQSettingKeys.RetrySeconds, 1);
        // PublisherAcknowledgements=true makes BasicPublishAsync wait for a broker confirm.
        // Our hanging-channel mock exploits this to stall the call until the timeout fires.
        transport.SetClientSetting(RabbitMQSettingKeys.PublisherAcknowledgements, true);

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("timeout-timing");

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        var producer = new Producer(
            transport,
            queue.Object,
            bus.Object,
            NullLogger<Producer>.Instance);

        // Seam 1: inject a mock connection whose channel hangs on BasicPublishAsync.
        // This replaces CreateConnectionAsync inside ProducerConnection, so the first
        // EnsureConnectedAsync call succeeds quickly (mock returns immediately) but every
        // BasicPublishAsync on the resulting channel blocks until the timeout CTS fires.
        // The slow-reconnect simulation is layered on top: after the FIRST connect, every
        // subsequent recreate (driven by reset-required) waits reconnectDelayMs. The
        // load-bearing property under test: this delay runs under _connectionSemaphore, NOT
        // _publishLock, so concurrent publishers are not serialised behind it.
        var hangingChannel = BuildHangingChannel();
        int connectCalls = 0;
        producer.CreateConnectionForTests = async (_, _, _, ct) =>
        {
            // Skip the delay on the first connect so the test setup doesn't pay the
            // reconnect cost. Subsequent calls (driven by post-timeout reset) simulate
            // a slow reconnect under _connectionSemaphore.
            if (Interlocked.Increment(ref connectCalls) > 1)
            {
                await Task.Delay(reconnectDelayMs, ct).ConfigureAwait(false);
            }
            var conn = new Mock<IConnection>();
            conn.SetupGet(c => c.IsOpen).Returns(true);
            conn.Setup(c => c.CreateChannelAsync(
                    It.IsAny<CreateChannelOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(hangingChannel.Object);
            return conn.Object;
        };

        // Act ─────────────────────────────────────────────────────────────────────────────────
        var stopwatches = Enumerable.Range(0, publisherCount)
            .Select(_ => new System.Diagnostics.Stopwatch())
            .ToList();

        var publishTasks = stopwatches.Select((sw, _) => Task.Run(async () =>
        {
            sw.Start();
            try
            {
                await producer.PublishAsync(typeof(object), new byte[] { 1, 2, 3 });
            }
            catch (TimeoutException)
            {
                // Expected: BasicPublishAsync hangs and the publish-timeout CTS fires.
                // The timeout is raised in PublishWithTimeoutAsync, _publishLock is released,
                // and elapsed time should be ~publishTimeout.
            }
            catch (Exception)
            {
                // Swallow other exceptions: the timing assertion below is the load-bearing
                // check, and a regression that pulls EnsureConnectedAsync back inside
                // _publishLock could surface a transient race (e.g. NullReferenceException
                // from a torn-down _model) rather than a clean timeout. Capture here so the
                // wall-clock regression remains visible rather than masked by an unrelated
                // throw type.
            }
            finally
            {
                sw.Stop();
            }
        })).ToList();

        await Task.WhenAll(publishTasks);

        await producer.DisposeAsync();

        // Assert ──────────────────────────────────────────────────────────────────────────────
        // Every publisher should return in ~publishTimeout ≈ 200 ms; threshold = 3 000 ms.
        // If EnsureConnectedAsync ran inside _publishLock, the slow recreate would serialise
        // publishers behind it, pushing publisher #N to ≥ N × (publishTimeout + reconnectDelay)
        // — the worst publisher would exceed 7 000 ms.
        for (var i = 0; i < publisherCount; i++)
        {
            Assert.True(
                stopwatches[i].Elapsed < assertThreshold,
                $"Publisher {i} took {stopwatches[i].Elapsed.TotalMilliseconds:F0}ms; " +
                $"expected < {assertThreshold.TotalMilliseconds:F0}ms. " +
                $"Exceeding the threshold indicates EnsureConnectedAsync is being awaited under " +
                $"_publishLock (worst-case ≈ {reconnectDelayMs * publisherCount}ms).");
        }
    }

    /// <summary>
    /// Builds a mock <see cref="IChannel"/> whose <c>BasicPublishAsync</c> hangs indefinitely
    /// until the caller's <see cref="CancellationToken"/> is cancelled. Because
    /// <c>PublishWithTimeoutAsync</c> passes a linked CTS that fires after <c>_publishTimeout</c>,
    /// every <c>BasicPublishAsync</c> call against this channel is guaranteed to time out,
    /// giving a deterministic timeout trigger without relying on broker latency or queue policies.
    /// </summary>
    private static Mock<IChannel> BuildHangingChannel()
    {
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<BasicProperties>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _, string _, bool _, BasicProperties _, ReadOnlyMemory<byte> _, CancellationToken ct) =>
                new ValueTask(Task.Delay(Timeout.Infinite, ct)));
        return channel;
    }

}
