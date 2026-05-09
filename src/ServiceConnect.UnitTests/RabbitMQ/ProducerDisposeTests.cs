using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Verifies that <see cref="Producer.DisposeAsync"/> always tears down the channel and
/// connection even when <c>_publishLock</c> cannot be acquired within the dispose timeout.
/// </summary>
public class ProducerDisposeTests
{
    private static Producer CreateProducer()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        return new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);
    }

    private static Producer CreateProducerWithRetrySettings(ushort retryCount, ushort retrySeconds)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.RetryCount] = retryCount,
            [RabbitMQSettingKeys.RetrySeconds] = retrySeconds,
        });

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        return new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);
    }

    private static void SetField<T>(Producer producer, string fieldName, T value) =>
        ProducerInternals.SetField(producer, fieldName, value);

    private static T GetField<T>(Producer producer, string fieldName) =>
        ProducerInternals.GetField<T>(producer, fieldName);

    [Fact]
    public async Task DisposeAsync_WhenPublishLockHeld_StillDisposesChannelAndConnection()
    {
        // Arrange
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        // IChannel.CloseAsync has a ushort/string/bool/CancellationToken overload that
        // the zero-arg extension method delegates to — mock that signature.
        channel.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(true);
        connection.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var producer = CreateProducer();

        // Inject mock channel/connection directly, bypassing real RabbitMQ.
        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connection", connection.Object);
        SetField(producer, "_connected", true);

        // Use the test-seam to shorten the dispose timeout so the test completes quickly
        // rather than waiting the production 30 seconds.
        SetField(producer, "DisposeTimeoutForTests", TimeSpan.FromMilliseconds(50));

        // Simulate a stuck publish: hold _publishLock so DisposeAsync cannot acquire it
        // within the short timeout, triggering the "lock timeout" path.
        var publishLock = GetField<SemaphoreSlim>(producer, "_publishLock");
        await publishLock.WaitAsync(); // take the lock — dispose will time out waiting

        // Act — should complete in ~50ms (timeout) rather than hanging.
        await producer.DisposeAsync();

        // The semaphore is not disposed by DisposeAsync, so Release() succeeds cleanly here.
        // This simulates the in-flight publisher's finally block completing its unwind.
        publishLock.Release();

        // Assert — channel and connection must always be closed even though the lock timed out.
        channel.Verify(c => c.CloseAsync(
            It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(c => c.CloseAsync(
            It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_WhenBothLocksHeld_RespectsSharedBudgetNotDoubled()
    {
        // Arrange — wire up mock channel/connection so teardown succeeds quickly.
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(true);
        connection.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var producer = CreateProducer();
        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connection", connection.Object);
        SetField(producer, "_connected", true);

        var disposeTimeout = TimeSpan.FromMilliseconds(150);
        SetField(producer, "DisposeTimeoutForTests", (TimeSpan?)disposeTimeout);

        // Hold BOTH semaphores so each WaitAsync(timeout) must time out.
        var publishLock = GetField<SemaphoreSlim>(producer, "_publishLock");
        var connectionSemaphore = GetField<SemaphoreSlim>(producer, "_connectionSemaphore");
        await publishLock.WaitAsync();
        await connectionSemaphore.WaitAsync();

        // Act — measure dispose wall time.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await producer.DisposeAsync();
        stopwatch.Stop();

        // The semaphores are not disposed by DisposeAsync, so Release() succeeds cleanly.
        // This simulates in-flight publishers completing their finally blocks after teardown.
        publishLock.Release();
        connectionSemaphore.Release();

        // The invariant under test: both lock waits share ONE 150ms budget, not
        // two stacked 150ms budgets. If they were stacked (the regression we're
        // guarding against) the elapsed wall time would be ≥ 300ms; sharing a
        // budget caps it well below that. The bound is 500ms rather than the
        // tighter ~250ms that "shared budget + jitter" would allow because the
        // test runs alongside other parallel test classes (and inside a CPU-
        // constrained cgroup on this dev machine), and Stopwatch is wall-clock
        // — scheduler stalls can easily add 100–300ms of jitter under load.
        // 50ms lower bound is a sanity check that the timers actually ran
        // (a pathological fast return would be near-zero).
        Assert.InRange(stopwatch.Elapsed,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task DisposeAsync_DoesNotDisposeSemaphores_AllowsInFlightPublisherCleanRelease()
    {
        // Arrange — set up a producer with mock channel/connection.
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(true);
        connection.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var producer = CreateProducer();
        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connection", connection.Object);
        SetField(producer, "_connected", true);
        SetField(producer, "DisposeTimeoutForTests", (TimeSpan?)TimeSpan.FromMilliseconds(50));

        // Simulate an in-flight publisher holding _publishLock — dispose times out
        // waiting for it.
        var publishLock = GetField<SemaphoreSlim>(producer, "_publishLock");
        await publishLock.WaitAsync();

        // Act
        await producer.DisposeAsync();

        // Assert — the in-flight publisher's finally block runs publishLock.Release().
        // The semaphore must remain alive so this Release() succeeds rather than
        // throwing ObjectDisposedException out of the publisher's unwind path.
        var ex = Record.Exception(() => publishLock.Release());
        Assert.Null(ex);
    }

    [Fact]
    public async Task PublishAsync_WhenDisposeRanWhileWaitingForLock_ThrowsObjectDisposedException()
    {
        // Arrange — producer with mock channel; simulate "publisher already past
        // EnsureConnectedAsync but still waiting on _publishLock" by holding the
        // lock from the test, then asynchronously kicking off PublishAsync.
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(true);
        connection.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var producer = CreateProducer();
        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connection", connection.Object);
        SetField(producer, "_connected", true);
        SetField(producer, "DisposeTimeoutForTests", (TimeSpan?)TimeSpan.FromMilliseconds(50));

        var publishLock = GetField<SemaphoreSlim>(producer, "_publishLock");
        await publishLock.WaitAsync(); // hold the lock; publisher will queue behind us

        // Kick off PublishAsync — it passes EnsureConnectedAsync (since _connected = true
        // and _disposed = 0) and then blocks on _publishLock.WaitAsync.
        var publishTask = producer.PublishAsync(typeof(TestPayload), new byte[] { 1, 2, 3 });

        // Run DisposeAsync — sets _disposed = 1, waits for the lock with the short
        // test timeout, gives up, tears down channel/connection, releases nothing
        // (publishLockAcquired = false), exits.
        await producer.DisposeAsync();

        // Now release the lock the test was holding — the publisher acquires it,
        // observes _disposed = 1, and must throw ObjectDisposedException rather
        // than NRE on null _model.
        publishLock.Release();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => publishTask);
    }

    [Fact]
    public async Task PublishAsync_WhenDisposedMidRetry_AbortsPromptlyInsteadOfBurningRetryBudget()
    {
        // Regression guard: when DisposeAsync flips _disposed while a publisher's retry loop
        // is mid-Task.Delay (the lock is released between attempts), the next attempt's
        // EnsureConnectedAsync throws ObjectDisposedException. That exception MUST surface
        // immediately. Pre-fix, the catch-when at the bottom of ExecuteRetryingPublishAsync
        // treated ObjectDisposedException as retriable and burned the full retryCount *
        // retrySeconds budget (default 60 * 10s = 10 min) against a permanently dead instance.
        //
        // Test parameters: retryCount=60, retrySeconds=1 — pre-fix would take up to ~60s,
        // post-fix returns within one Task.Delay window (~1s) plus dispose teardown.
        var producer = CreateProducerWithRetrySettings(retryCount: 60, retrySeconds: 1);

        // Pre-seed a healthy mock channel so the first publisher iteration's
        // EnsureConnectedAsync is a fast lock-free fast-path (no CreateConnectionAsync,
        // no inner Retry loop). The publish itself then fails with a retriable exception,
        // sending the publisher into the inter-attempt Task.Delay window.
        var publishStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
            .Returns(() =>
            {
                publishStarted.TrySetResult();
                throw new InvalidOperationException("simulated retriable publish failure");
            });
        // Mock channel close so dispose-side teardown does not hit a strict-mock invocation.
        channel.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connected", true);

        // Keep DisposeAsync responsive — its lock-wait budget should not dominate the observed
        // wall clock.
        SetField(producer, "DisposeTimeoutForTests", (TimeSpan?)TimeSpan.FromMilliseconds(50));

        var publishTask = Task.Run(() =>
            producer.PublishAsync(typeof(TestPayload), new byte[] { 1, 2, 3 }));

        // Wait for the first BasicPublishAsync invocation. The publisher then enters the
        // catch-when arm: MarkResetRequired + Task.Delay(retrySeconds=1s).
        await publishStarted.Task;

        // Small buffer so the publisher is definitely inside Task.Delay rather than mid-throw.
        await Task.Delay(100);

        // Dispose: flips _disposed. The next iteration's EnsureConnectedAsync (which runs
        // OUTSIDE _publishLock) will throw ObjectDisposedException from its disposed pre-check.
        // Note: DisposeAsync also runs concurrently with whatever Task.Delay the publisher is
        // still in; that delay shares the publisher's caller token, which is the publish task's
        // ambient token (CancellationToken.None here), so the delay completes naturally.
        await producer.DisposeAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await publishTask);
        sw.Stop();

        // Tolerance: one full retrySeconds=1s for the inter-attempt Task.Delay window the
        // publisher may already be inside, plus generous headroom for scheduler jitter under
        // the cgroup CPU quota. Pre-fix this would be ~60s. Anything < 5s proves the disposed
        // catch fired and short-circuited the retry loop.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Publish task took {sw.Elapsed} after dispose; pre-fix this was ~60s. " +
            "ObjectDisposedException must short-circuit the retry loop, not be treated as retriable.");

        // The surfaced exception is ObjectDisposedException — directly from EnsureConnectedAsync's
        // disposed pre-check, propagated by the new explicit catch arm.
        Assert.IsType<ObjectDisposedException>(ex);
    }

    // Minimal payload type for PublishAsync's `Type` argument; PublishAsync only uses
    // it to compute an exchange name, so any class works.
    private sealed class TestPayload { }
}
