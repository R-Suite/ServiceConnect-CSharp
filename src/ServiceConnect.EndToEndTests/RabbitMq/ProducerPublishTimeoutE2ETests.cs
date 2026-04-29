using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Configuration;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.EndToEndTests.RabbitMq;

/// <summary>
/// Verifies the H22 contract end-to-end: when a publish times out, the producer releases
/// <c>_publishLock</c> immediately without driving a reconnect under that lock. Concurrent
/// publishers must not be stalled behind a single timed-out publisher's reconnect budget.
///
/// The test uses two injected seams:
/// <list type="bullet">
///   <item><description>
///     <c>CreateConnectionForTests</c> returns a mock connection whose channel hangs on
///     <c>BasicPublishAsync</c> indefinitely (until the publish-timeout CTS fires), giving
///     a deterministic timeout on every publish without requiring broker-level flow control.
///   </description></item>
///   <item><description>
///     <c>ReconnectForTests</c> introduces a fixed delay that simulates a slow broker
///     reconnect. Pre-fix, this delay ran under <c>_publishLock</c>, stalling all other
///     publishers. Post-fix, it runs off-lock (inside <c>EnsureConnectedAsync</c> before the
///     lock is acquired) and does not stall concurrent publishers.
///   </description></item>
/// </list>
/// The Testcontainers RabbitMQ container is started by the collection fixture, confirming this
/// is an integration-class test; the seams only replace the channel and reconnect internals.
/// </summary>
[Collection(nameof(MessagingCollection))]
public sealed class ProducerPublishTimeoutE2ETests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ConcurrentPublishers_WhenPublishTimesOut_NoPublisherBlockedForReconnectBudget()
    {
        // Arrange ─────────────────────────────────────────────────────────────────────────────
        //   publishTimeout   = 200 ms  (how long BasicPublishAsync hangs before timeout fires)
        //   reconnectDelay   = 2 000 ms (simulated slow reconnect after the timeout)
        //   publisherCount   = 5
        //
        // Post-fix (correct) timeline per publisher:
        //   Each publisher serializes through _publishLock.  The lock holder times out in
        //   ~publishTimeout ms, calls MarkResetRequired() (synchronous, <1 ms), and releases.
        //   The reconnect runs off-lock; it is triggered by the next EnsureConnectedAsync call
        //   which happens BEFORE _publishLock.WaitAsync in the following publish (not here —
        //   each publisher publishes once).  So the reconnect delay does not add to any
        //   publisher's wall-clock time in this test.
        //   Total time for all 5 publishers ≈ 5 × publishTimeout ≈ 1 000 ms.
        //
        // Pre-fix (regressed) timeline per publisher:
        //   The lock holder times out then calls await ReconnectAsync(…) UNDER _publishLock.
        //   ReconnectAsync takes reconnectDelay = 2 000 ms.  Every subsequent publisher has to
        //   wait for the current holder's reconnect before it can enter.  Publisher #5's elapsed
        //   time ≈ 4 × (publishTimeout + reconnectDelay) + publishTimeout + reconnectDelay
        //        ≈ 5 × 2 200 ms ≈ 11 000 ms.
        //
        // Threshold = 3 000 ms: well above the post-fix ~1 000 ms and well below the pre-fix
        // ~11 000 ms, leaving ample CI headroom on either side.
        const int publisherCount = 5;
        const int publishTimeoutMs = 200;
        const int reconnectDelayMs = 2_000;
        var assertThreshold = TimeSpan.FromMilliseconds(3_000);

        var transport = new TransportConfiguration
        {
            // The real Testcontainers broker is reachable, but the channel is replaced by the
            // hanging mock, so no AMQP traffic flows during the publish phase of this test.
            Host = _fixture.RabbitMqHostname,
            Username = _fixture.RabbitMqUsername,
            Password = _fixture.RabbitMqPassword,
        };
        transport.SetClientSetting(RabbitMQSettingKeys.Port, _fixture.RabbitMqPort);
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
        queue.SetupGet(q => q.QueueName).Returns("timeout-e2e");

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

        // Seam 2: slow-reconnect simulation.
        // Pre-fix: ReconnectAsync is awaited inside PublishWithTimeoutAsync while _publishLock is
        // held — this delay blocks all queued publishers for reconnectDelayMs per timed-out publish.
        // Post-fix: ReconnectAsync is only driven from EnsureConnectedAsync (before _publishLock)
        // and only by the first caller that wins the CAS on _resetRequired.  In this single-publish
        // test that call never runs during the publish phase, so the delay does not contribute.
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
                await producer.PublishAsync(typeof(object), [1, 2, 3]);
            }
            catch (TimeoutException)
            {
                // Expected post-fix: BasicPublishAsync hangs and the publish-timeout CTS fires.
                // The timeout is raised in PublishWithTimeoutAsync, _publishLock is released,
                // and elapsed time should be ~publishTimeout.
            }
            catch (Exception)
            {
                // Pre-fix: the in-lock ReconnectAsync path may produce exceptions other than
                // TimeoutException (e.g. NullReferenceException from a null channel after
                // DisposeConnectionAsync clears _model). Capture here so the timing assertion
                // below can report the actual wall-clock regression rather than surfacing the
                // unrelated NRE as the test failure.
            }
            finally
            {
                sw.Stop();
            }
        })).ToList();

        await Task.WhenAll(publishTasks);

        await producer.DisposeAsync();

        // Assert ──────────────────────────────────────────────────────────────────────────────
        // Post-fix: every publisher returns in ~publishTimeout ≈ 200ms; threshold = 3 000ms.
        // Pre-fix: publisher #5 returns in ≥ 5 × (publishTimeout + reconnectDelay) ≈ 11 000ms.
        for (var i = 0; i < publisherCount; i++)
        {
            Assert.True(
                stopwatches[i].Elapsed < assertThreshold,
                $"Publisher {i} took {stopwatches[i].Elapsed.TotalMilliseconds:F0}ms; " +
                $"expected < {assertThreshold.TotalMilliseconds:F0}ms. " +
                $"Exceeding the threshold indicates the H22 regression: ReconnectAsync is being " +
                $"awaited under _publishLock (pre-fix worst case ≈ {reconnectDelayMs * publisherCount}ms).");
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
