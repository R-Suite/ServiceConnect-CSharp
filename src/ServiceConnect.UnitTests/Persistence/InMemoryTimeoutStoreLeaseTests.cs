using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class InMemoryTimeoutStoreLeaseTests
{
    [Fact]
    public async Task RemoveDispatchedTimeoutAsync_LeaseAware_ThrowsWhenLeaseIsStale()
    {
        // Mirror of the Mongo regression guard: a caller with a stale lockOwner must see
        // a ConcurrencyException instead of a silent no-op. Parity across the two stores
        // keeps test doubles against the InMemory implementation honest.
        var now = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(timeProvider: time);

        var id = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData { Id = id, Time = now.AddMinutes(-1) });

        // Claim to establish a real LockedBy (the current owner, sessionA).
        var batch = await store.GetTimeoutsBatchAsync();
        var claimed = Assert.Single(batch.DueTimeouts);
        Assert.NotEqual(Guid.Empty, claimed.LockedBy);

        // A different caller tries to Remove with a Guid that no one owns.
        var staleOwner = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() =>
            store.RemoveDispatchedTimeoutAsync(id, lockOwner: staleOwner));

        Assert.Contains(id.ToString(), ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(staleOwner.ToString(), ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseDispatchedTimeoutAsync_LeaseAware_ThrowsWhenLeaseIsStale()
    {
        var now = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(timeProvider: time);

        var id = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData { Id = id, Time = now.AddMinutes(-1) });

        var batch = await store.GetTimeoutsBatchAsync();
        var claimed = Assert.Single(batch.DueTimeouts);

        var staleOwner = Guid.NewGuid();
        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() =>
            store.ReleaseDispatchedTimeoutAsync(id, lockOwner: staleOwner));

        Assert.Contains(id.ToString(), ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(staleOwner.ToString(), ex.Message, StringComparison.OrdinalIgnoreCase);

        // The real owner's lease must remain intact — Release from a stale owner is a no-op
        // from a state perspective.
        var secondBatch = await store.GetTimeoutsBatchAsync();
        Assert.Empty(secondBatch.DueTimeouts);
    }

    [Fact]
    public async Task RemoveDispatchedTimeoutAsync_LeaseAware_SucceedsForCorrectOwner()
    {
        var now = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(timeProvider: time);

        var id = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData { Id = id, Time = now.AddMinutes(-1) });

        var batch = await store.GetTimeoutsBatchAsync();
        var claimed = Assert.Single(batch.DueTimeouts);

        // Actual owner performs Remove — no throw and the timeout is gone from the store.
        var ex = await Record.ExceptionAsync(() =>
            store.RemoveDispatchedTimeoutAsync(id, lockOwner: claimed.LockedBy));
        Assert.Null(ex);

        var nextBatch = await store.GetTimeoutsBatchAsync();
        Assert.Empty(nextBatch.DueTimeouts);
    }

    [Fact]
    public async Task RemoveDispatchedTimeoutAsync_LeaseAware_ThrowsWhenRowIsUnleased()
    {
        // Parity guard: passing Guid.Empty (or any owner) against an unleased row must throw,
        // matching Mongo's filter which requires Locked == true. Without the !Locked check,
        // InMemory would silently succeed because the default LockedBy on an unleased row
        // is also Guid.Empty.
        var now = new DateTimeOffset(2026, 4, 22, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(timeProvider: time);
        var id = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData { Id = id, Time = now.AddMinutes(-1) });
        // Do NOT claim — row is unleased (Locked=false, LockedBy=Guid.Empty).

        await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.RemoveDispatchedTimeoutAsync(id, lockOwner: Guid.Empty));
    }

    [Fact]
    public async Task ReleaseDispatchedTimeoutAsync_LeaseAware_ThrowsWhenRowIsUnleased()
    {
        // Parity guard: same as above but for Release. An unleased row must not be writable
        // by a caller passing Guid.Empty, because Mongo's filter would reject it.
        var now = new DateTimeOffset(2026, 4, 22, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(timeProvider: time);
        var id = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData { Id = id, Time = now.AddMinutes(-1) });

        await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.ReleaseDispatchedTimeoutAsync(id, lockOwner: Guid.Empty));
    }

    [Fact]
    public async Task RemoveDispatchedTimeoutAsync_LeaseAware_PreCancelledToken_Throws()
    {
        var store = new InMemoryTimeoutStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.RemoveDispatchedTimeoutAsync(Guid.NewGuid(), lockOwner: Guid.NewGuid(), cts.Token));
    }

    [Fact]
    public async Task ReleaseDispatchedTimeoutAsync_LeaseAware_PreCancelledToken_Throws()
    {
        var store = new InMemoryTimeoutStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.ReleaseDispatchedTimeoutAsync(Guid.NewGuid(), lockOwner: Guid.NewGuid(), cts.Token));
    }
}
