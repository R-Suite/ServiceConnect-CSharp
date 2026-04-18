using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests;

public class InMemoryTimeoutStoreTests
{
    [Fact]
    public async Task GetTimeoutsBatch_OnlyDueTimeouts_AreReturned()
    {
        var now = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(timeProvider: time);

        await store.InsertTimeoutAsync(new TimeoutData { Id = Guid.NewGuid(), Time = now.AddMinutes(-5) });    // due
        await store.InsertTimeoutAsync(new TimeoutData { Id = Guid.NewGuid(), Time = now.AddMinutes(-1) });    // due
        await store.InsertTimeoutAsync(new TimeoutData { Id = Guid.NewGuid(), Time = now.AddMinutes(5) });     // future

        var batch = await store.GetTimeoutsBatchAsync();
        Assert.Equal(2, batch.DueTimeouts.Count);
    }

    [Fact]
    public async Task GetTimeoutsBatch_NextQueryTime_PointsAtEarliestFuture()
    {
        var now = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(timeProvider: time);

        var expectedNext = now.AddSeconds(30);
        await store.InsertTimeoutAsync(new TimeoutData { Id = Guid.NewGuid(), Time = expectedNext });
        await store.InsertTimeoutAsync(new TimeoutData { Id = Guid.NewGuid(), Time = now.AddMinutes(5) });

        var batch = await store.GetTimeoutsBatchAsync();
        Assert.Empty(batch.DueTimeouts);
        Assert.Equal(expectedNext, batch.NextQueryTime);
    }

    [Fact]
    public async Task RemoveDispatchedTimeout_RemovesFromIndex_SoSubsequentPollSkipsIt()
    {
        var now = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(timeProvider: time);

        var id = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData { Id = id, Time = now.AddMinutes(-1) });

        var first = await store.GetTimeoutsBatchAsync();
        Assert.Single(first.DueTimeouts);

        await store.RemoveDispatchedTimeoutAsync(id);

        var second = await store.GetTimeoutsBatchAsync();
        Assert.Empty(second.DueTimeouts);
    }

    [Fact]
    public async Task ReleaseDispatchedTimeout_ClearsLockFields_OnExistingEntry()
    {
        var now = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(timeProvider: time);

        var id = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = id,
            Time = now.AddMinutes(-1),
            Locked = true,
            LockedBy = Guid.NewGuid(),
            LockExpiresAt = now.AddMinutes(5),
        });

        await store.ReleaseDispatchedTimeoutAsync(id);

        var batch = await store.GetTimeoutsBatchAsync();
        var fetched = Assert.Single(batch.DueTimeouts);
        Assert.Equal(id, fetched.Id);
        Assert.False(fetched.Locked);
        Assert.Equal(Guid.Empty, fetched.LockedBy);
        Assert.Null(fetched.LockExpiresAt);
    }

    [Fact]
    public async Task ReleaseDispatchedTimeout_UnknownId_DoesNotThrow()
    {
        var store = new InMemoryTimeoutStore();

        var exception = await Record.ExceptionAsync(() =>
            store.ReleaseDispatchedTimeoutAsync(Guid.NewGuid()));

        Assert.Null(exception);
    }

    [Fact]
    public async Task ReleaseDispatchedTimeout_PreCancelledToken_Throws()
    {
        var store = new InMemoryTimeoutStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.ReleaseDispatchedTimeoutAsync(Guid.NewGuid(), cts.Token));
    }
}
