using System.Reflection;
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
        // and _disposedInt = 0) and then blocks on _publishLock.WaitAsync.
        var publishTask = producer.PublishAsync(typeof(TestPayload), [1, 2, 3]);

        // Run DisposeAsync — sets _disposedInt = 1, waits for the lock with the short
        // test timeout, gives up, tears down channel/connection, releases nothing
        // (publishLockAcquired = false), exits.
        await producer.DisposeAsync();

        // Now release the lock the test was holding — the publisher acquires it,
        // observes _disposedInt = 1, and must throw ObjectDisposedException rather
        // than NRE on null _model.
        publishLock.Release();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => publishTask);
    }

    // Minimal payload type for PublishAsync's `Type` argument; PublishAsync only uses
    // it to compute an exchange name, so any class works.
    private sealed class TestPayload { }
}
