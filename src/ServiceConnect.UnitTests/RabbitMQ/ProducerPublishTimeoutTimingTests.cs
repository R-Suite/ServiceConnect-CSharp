using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Asserts the H22 user-visible wall-clock contract: a publish timeout returns to the caller
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
        // Threshold = 3 000 ms. Awaiting ReconnectAsync under _publishLock would push
        // the worst publisher above 7 s; the off-lock reconnect path keeps it near ~1 s.
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
        };
        transport.SetClientSetting(RabbitMQSettingKeys.Port, 5672);
        transport.SetClientSetting(RabbitMQSettingKeys.PublishTimeout, TimeSpan.FromMilliseconds(publishTimeoutMs));
        // RetryCount / RetrySeconds bound how long EnsureConnectedAsync retries if
        // CreateConnectionAsync throws.  They do not affect the ReconnectForTests path but must
        // be set to non-default values so the test is clearly scoped.
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
        var hangingChannel = BuildHangingChannel();
        producer.CreateConnectionForTests = (_, _, _, _) =>
        {
            var conn = new Mock<IConnection>();
            conn.SetupGet(c => c.IsOpen).Returns(true);
            conn.Setup(c => c.CreateChannelAsync(
                    It.IsAny<CreateChannelOptions?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(hangingChannel.Object);
            return Task.FromResult(conn.Object);
        };

        // Seam 2: slow-reconnect simulation. ReconnectAsync is only driven from
        // EnsureConnectedAsync (before _publishLock is acquired) and only by the first
        // caller that wins the CAS on _resetRequired. In this single-publish test that
        // call never runs during the publish phase, so the delay does not contribute.
        // Awaiting ReconnectAsync inside PublishWithTimeoutAsync while _publishLock is
        // held would block all queued publishers for reconnectDelayMs per timed-out publish.
        producer.ReconnectForTests = ct => Task.Delay(reconnectDelayMs, ct);

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
                // Swallow other exceptions (e.g. an in-lock ReconnectAsync path could
                // surface a NullReferenceException from a null _model after DisposeConnectionAsync).
                // The timing assertion below is the load-bearing check; capturing here keeps
                // the wall-clock regression visible rather than surfacing an unrelated NRE.
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
        // Serialising publishers behind an in-lock ReconnectAsync would push publisher #N to
        // ≥ N × (publishTimeout + reconnectDelay) — the worst publisher would exceed 7 000 ms.
        for (var i = 0; i < publisherCount; i++)
        {
            Assert.True(
                stopwatches[i].Elapsed < assertThreshold,
                $"Publisher {i} took {stopwatches[i].Elapsed.TotalMilliseconds:F0}ms; " +
                $"expected < {assertThreshold.TotalMilliseconds:F0}ms. " +
                $"Exceeding the threshold indicates ReconnectAsync is being awaited under " +
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
