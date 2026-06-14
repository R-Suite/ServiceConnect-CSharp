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
        // Mirror of the Mongo lease test: a caller with a stale lockOwner must see a
        // ConcurrencyException instead of a silent no-op. Parity across the two stores
        // keeps test doubles against the InMemory implementation honest.
        var now = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions(), timeProvider: time);

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
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions(), timeProvider: time);

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
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions(), timeProvider: time);

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
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions(), timeProvider: time);
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
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions(), timeProvider: time);
        var id = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData { Id = id, Time = now.AddMinutes(-1) });

        await Assert.ThrowsAsync<ConcurrencyException>(
            () => store.ReleaseDispatchedTimeoutAsync(id, lockOwner: Guid.Empty));
    }

    [Fact]
    public async Task RemoveDispatchedTimeoutAsync_LeaseAware_PreCancelledToken_Throws()
    {
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.RemoveDispatchedTimeoutAsync(Guid.NewGuid(), lockOwner: Guid.NewGuid(), cts.Token));
    }

    [Fact]
    public async Task ReleaseDispatchedTimeoutAsync_LeaseAware_PreCancelledToken_Throws()
    {
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.ReleaseDispatchedTimeoutAsync(Guid.NewGuid(), lockOwner: Guid.NewGuid(), cts.Token));
    }

    [Fact]
    public async Task RemoveDispatchedTimeoutAsync_NonNullLockOwner_MissingRow_ThrowsConcurrencyException()
    {
        // A non-null lockOwner against a missing row must throw, not silently no-op.
        // A missing row's "current lock owner" is nobody, so the caller's lease is
        // already invalidated — surface as ConcurrencyException to match Mongo.
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions());
        var randomId = Guid.NewGuid();
        var ownerThatNeverHadIt = Guid.NewGuid();

        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            store.RemoveDispatchedTimeoutAsync(randomId, lockOwner: ownerThatNeverHadIt));
    }

    [Fact]
    public async Task ReleaseDispatchedTimeoutAsync_NonNullLockOwner_MissingRow_ThrowsConcurrencyException()
    {
        // A non-null lockOwner against a missing row must throw, not silently no-op.
        // Mirrors RemoveDispatchedTimeoutAsync contract and Mongo behaviour.
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions());
        var randomId = Guid.NewGuid();
        var ownerThatNeverHadIt = Guid.NewGuid();

        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            store.ReleaseDispatchedTimeoutAsync(randomId, lockOwner: ownerThatNeverHadIt));
    }

    [Fact]
    public async Task RemoveDispatchedTimeout_ExpiredLease_ThrowsConcurrencyException()
    {
        // The caller held a valid lease at claim time, but the 5-minute window has
        // elapsed before dispatching.  An expired lease is as invalid as a mismatched
        // owner — the InMemory store must mirror the Mongo contract.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions(), timeProvider: clock);

        var id = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = id,
            Destination = "dest",
            ProcessManagerId = Guid.NewGuid(),
            Time = clock.GetUtcNow(),
            Headers = new Dictionary<string, object>(StringComparer.Ordinal),
        });

        var batch = await store.GetTimeoutsBatchAsync();
        Assert.Single(batch.DueTimeouts);
        var owner = batch.DueTimeouts[0].LockedBy;

        // Advance past the 5-minute default lease.
        clock.Advance(TimeSpan.FromMinutes(6));

        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            store.RemoveDispatchedTimeoutAsync(id, owner));
    }

    [Fact]
    public async Task ReleaseDispatchedTimeout_ExpiredLease_ThrowsConcurrencyException()
    {
        // Mirror of the Remove test: ReleaseDispatchedTimeoutAsync must also reject
        // expired leases rather than silently clearing the lock on a stale claim.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions(), timeProvider: clock);

        var id = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = id,
            Destination = "dest",
            ProcessManagerId = Guid.NewGuid(),
            Time = clock.GetUtcNow(),
            Headers = new Dictionary<string, object>(StringComparer.Ordinal),
        });

        var batch = await store.GetTimeoutsBatchAsync();
        var owner = batch.DueTimeouts[0].LockedBy;

        clock.Advance(TimeSpan.FromMinutes(6));

        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            store.ReleaseDispatchedTimeoutAsync(id, owner));
    }

    [Fact]
    public async Task RemoveDispatchedTimeoutAsync_NullLockOwner_RemovesLeasedRow()
    {
        // lockOwner == null is the unconditional id-only path. A leased row must still
        // be removed when the caller explicitly opts out of the lease check by passing null.
        var now = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions(), timeProvider: time);

        var id = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData { Id = id, Time = now.AddMinutes(-1) });

        // Claim so the row is leased.
        var batch = await store.GetTimeoutsBatchAsync();
        Assert.Contains(batch.DueTimeouts, t => t.Id == id);

        await store.RemoveDispatchedTimeoutAsync(id, lockOwner: null);

        var afterBatch = await store.GetTimeoutsBatchAsync();
        Assert.DoesNotContain(afterBatch.DueTimeouts, t => t.Id == id);
    }
}
