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

        // Release the simulated stuck publish ONLY if the semaphore wasn't disposed by DisposeAsync.
        // (After the fix, DisposeAsync disposes the semaphore in its finally block regardless of
        // whether it acquired the lock; so we swallow ObjectDisposedException here.)
        try { publishLock.Release(); } catch (ObjectDisposedException) { }

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

        // DisposeAsync disposes the semaphores in its finally block regardless of
        // whether it acquired them, so Release() may throw ObjectDisposedException.
        try { publishLock.Release(); } catch (ObjectDisposedException) { }
        try { connectionSemaphore.Release(); } catch (ObjectDisposedException) { }

        // Both waits share one stopwatch budget, so total elapsed is bounded by
        // disposeTimeout (150ms) plus teardown overhead. 220ms upper bound = 150ms
        // timeout + 70ms slack for mock-channel close + scheduler jitter. 120ms
        // lower bound guards against a pathological fast return (timers never
        // fire early, so this is a sanity check that the waits actually ran).
        Assert.InRange(stopwatch.Elapsed,
            TimeSpan.FromMilliseconds(120),
            TimeSpan.FromMilliseconds(220));
    }
}
