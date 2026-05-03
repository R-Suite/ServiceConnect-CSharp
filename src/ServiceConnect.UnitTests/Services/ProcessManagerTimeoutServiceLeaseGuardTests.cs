using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class ProcessManagerTimeoutServiceLeaseGuardTests
{
    [Fact]
    public async Task PollOnceAsync_LeaseExpiredDuringSend_DoesNotCallRemove()
    {
        // Arrange a timeout whose lease expires DURING SendAsync — the SendAsync
        // mock callback advances FakeTimeProvider so the post-send lease check
        // sees expiration. Pre-fix Remove ran unconditionally; post-fix it must skip,
        // leaving reclaim to the lease-expiry sweep on the next poll.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero));
        var bus = new Mock<IBus>();
        var store = new Mock<ITimeoutStore>();
        var config = new Mock<IBusConfiguration>();
        config.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var lockOwner = Guid.NewGuid();
        var timeoutId = Guid.NewGuid();

        var dueTimeout = new TimeoutData
        {
            Id = timeoutId,
            ProcessManagerId = Guid.NewGuid(),
            Destination = "test-queue",
            Time = clock.GetUtcNow().AddMinutes(-1),
            Headers = new Dictionary<string, object>(),
            Locked = true,
            LockedBy = lockOwner,
            // Lease expires 10 s from now — safe before SendAsync, expired after.
            LockExpiresAt = clock.GetUtcNow().Add(TimeSpan.FromSeconds(10))
        };

        store.Setup(s => s.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new TimeoutsBatch { DueTimeouts = [dueTimeout] });

        // SendAsync advances the clock by 30 s — well past the 10 s LockExpiresAt window.
        bus.Setup(b => b.SendAsync(It.IsAny<TimeoutMessage>(), It.IsAny<SendOptions>(), It.IsAny<CancellationToken>()))
           .Callback(() => clock.Advance(TimeSpan.FromSeconds(30)))
           .Returns(Task.CompletedTask);

        var svc = new ProcessManagerTimeoutService(
            config.Object,
            new Lazy<IBus>(() => bus.Object),
            store.Object,
            NullLogger<ProcessManagerTimeoutService>.Instance,
            clock);

        await svc.PollOnceAsync();

        bus.Verify(
            b => b.SendAsync(It.IsAny<TimeoutMessage>(), It.IsAny<SendOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
        store.Verify(
            s => s.RemoveDispatchedTimeoutAsync(timeoutId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Lease expired during SendAsync; Remove must NOT be called — let the lease-expiry sweep reclaim the row.");
    }

    [Fact]
    public async Task PollOnceAsync_LeaseValidAfterSend_CallsRemove()
    {
        // Regression guard: when the lease is still valid after SendAsync returns the
        // happy-path Remove must still fire exactly once.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 3, 12, 0, 0, TimeSpan.Zero));
        var bus = new Mock<IBus>();
        var store = new Mock<ITimeoutStore>();
        var config = new Mock<IBusConfiguration>();
        config.Setup(c => c.EnableProcessManagerTimeouts).Returns(true);

        var lockOwner = Guid.NewGuid();
        var timeoutId = Guid.NewGuid();

        var dueTimeout = new TimeoutData
        {
            Id = timeoutId,
            ProcessManagerId = Guid.NewGuid(),
            Destination = "test-queue",
            Time = clock.GetUtcNow().AddMinutes(-1),
            Headers = new Dictionary<string, object>(),
            Locked = true,
            LockedBy = lockOwner,
            // Lease expires 5 minutes from now — easily survives a realistic SendAsync.
            LockExpiresAt = clock.GetUtcNow().Add(TimeSpan.FromMinutes(5))
        };

        store.Setup(s => s.GetTimeoutsBatchAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new TimeoutsBatch { DueTimeouts = [dueTimeout] });

        // SendAsync does not advance the clock — lease remains valid.
        bus.Setup(b => b.SendAsync(It.IsAny<TimeoutMessage>(), It.IsAny<SendOptions>(), It.IsAny<CancellationToken>()))
           .Returns(Task.CompletedTask);

        var svc = new ProcessManagerTimeoutService(
            config.Object,
            new Lazy<IBus>(() => bus.Object),
            store.Object,
            NullLogger<ProcessManagerTimeoutService>.Instance,
            clock);

        await svc.PollOnceAsync();

        bus.Verify(
            b => b.SendAsync(It.IsAny<TimeoutMessage>(), It.IsAny<SendOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
        store.Verify(
            s => s.RemoveDispatchedTimeoutAsync(timeoutId, (Guid?)lockOwner, It.IsAny<CancellationToken>()),
            Times.Once,
            "Lease still valid post-send; Remove is the expected at-least-once dispatch ack.");
    }
}
