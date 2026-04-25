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
        Assert.All(batch.DueTimeouts, timeout =>
        {
            Assert.True(timeout.Locked);
            Assert.NotEqual(Guid.Empty, timeout.LockedBy);
            Assert.NotNull(timeout.LockExpiresAt);
        });
    }

    [Fact]
    public async Task GetTimeoutsBatch_DueTimeoutIsClaimed_AndHiddenUntilReleased()
    {
        var now = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(timeProvider: time);

        var id = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData { Id = id, Time = now.AddMinutes(-1) });

        var first = await store.GetTimeoutsBatchAsync();
        var claimed = Assert.Single(first.DueTimeouts);
        Assert.Equal(id, claimed.Id);
        Assert.True(claimed.Locked);
        Assert.NotEqual(Guid.Empty, claimed.LockedBy);
        Assert.Equal(now.AddMinutes(5), claimed.LockExpiresAt);

        var second = await store.GetTimeoutsBatchAsync();
        Assert.Empty(second.DueTimeouts);
    }

    [Fact]
    public async Task GetTimeoutsBatch_ReturnedTimeoutMutation_DoesNotChangeStoredLeaseState()
    {
        var now = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(timeProvider: time);

        var id = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData { Id = id, Time = now.AddMinutes(-1) });

        var first = await store.GetTimeoutsBatchAsync();
        var claimed = Assert.Single(first.DueTimeouts);

        claimed.Locked = false;
        claimed.LockedBy = Guid.Empty;
        claimed.LockExpiresAt = null;

        var second = await store.GetTimeoutsBatchAsync();
        Assert.Empty(second.DueTimeouts);
    }

    [Fact]
    public async Task TimeoutHeaders_MutableValues_AreIsolatedFromStoredTimeoutData()
    {
        var now = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(timeProvider: time);

        var id = Guid.NewGuid();
        var headerValue = new byte[] { 1, 2, 3 };

        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = id,
            Time = now.AddMinutes(-1),
            Headers = new Dictionary<string, object> { ["payload"] = headerValue },
        });

        headerValue[0] = 9;

        var first = await store.GetTimeoutsBatchAsync();
        var claimed = Assert.Single(first.DueTimeouts);
        var claimedHeader = Assert.IsType<byte[]>(claimed.Headers["payload"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, claimedHeader);

        claimedHeader[1] = 8;

        await store.ReleaseDispatchedTimeoutAsync(id);

        var second = await store.GetTimeoutsBatchAsync();
        var reclaimed = Assert.Single(second.DueTimeouts);
        var reclaimedHeader = Assert.IsType<byte[]>(reclaimed.Headers["payload"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, reclaimedHeader);
    }

    [Fact]
    public async Task ReleaseDispatchedTimeout_ReleasedTimeoutIsReturnedAgainOnNextPoll()
    {
        var now = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore(timeProvider: time);

        var id = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData { Id = id, Time = now.AddMinutes(-1) });

        var first = await store.GetTimeoutsBatchAsync();
        var initiallyClaimed = Assert.Single(first.DueTimeouts);
        var initialLockOwner = initiallyClaimed.LockedBy;

        await store.ReleaseDispatchedTimeoutAsync(id);

        var second = await store.GetTimeoutsBatchAsync();
        var reclaimed = Assert.Single(second.DueTimeouts);
        Assert.Equal(id, reclaimed.Id);
        Assert.True(reclaimed.Locked);
        Assert.NotEqual(Guid.Empty, reclaimed.LockedBy);
        Assert.NotNull(reclaimed.LockExpiresAt);
        Assert.NotEqual(initialLockOwner, reclaimed.LockedBy);
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
    public async Task ReleaseDispatchedTimeout_ClearsLockFields_BeforeNextClaim()
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
        Assert.True(fetched.Locked);
        Assert.NotEqual(Guid.Empty, fetched.LockedBy);
        Assert.NotNull(fetched.LockExpiresAt);
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
            store.ReleaseDispatchedTimeoutAsync(Guid.NewGuid(), cancellationToken: cts.Token));
    }

    [Fact]
    public async Task GetTimeoutsBatchAsync_WithBatchSize_HonoursCap()
    {
        var now = new DateTimeOffset(2026, 4, 26, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore("", "", timeProvider: time);
        for (int i = 0; i < 50; i++)
            await store.InsertTimeoutAsync(new TimeoutData
            {
                Id = Guid.NewGuid(),
                Destination = "dest",
                ProcessManagerId = Guid.NewGuid(),
                Time = time.GetUtcNow().AddMinutes(-1),
                Headers = new Dictionary<string, object>(),
            }, CancellationToken.None);

        var batch = await store.GetTimeoutsBatchAsync(batchSize: 10);

        Assert.Equal(10, batch.DueTimeouts.Count);
    }

    [Fact]
    public async Task GetTimeoutsBatchAsync_NullBatchSize_ReturnsAllDueTimeouts()
    {
        var now = new DateTimeOffset(2026, 4, 26, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore("", "", timeProvider: time);
        for (int i = 0; i < 5; i++)
            await store.InsertTimeoutAsync(new TimeoutData
            {
                Id = Guid.NewGuid(),
                Time = time.GetUtcNow().AddMinutes(-1),
                Headers = new Dictionary<string, object>(),
            });

        var batch = await store.GetTimeoutsBatchAsync();

        Assert.Equal(5, batch.DueTimeouts.Count);
    }

    [Fact]
    public async Task GetTimeoutsBatchAsync_BatchSizeLargerThanDueCount_ReturnsAllDueTimeouts()
    {
        var now = new DateTimeOffset(2026, 4, 26, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var store = new InMemoryTimeoutStore("", "", timeProvider: time);
        for (int i = 0; i < 5; i++)
            await store.InsertTimeoutAsync(new TimeoutData
            {
                Id = Guid.NewGuid(),
                Time = time.GetUtcNow().AddMinutes(-1),
                Headers = new Dictionary<string, object>(),
            });

        var batch = await store.GetTimeoutsBatchAsync(batchSize: 100);

        Assert.Equal(5, batch.DueTimeouts.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetTimeoutsBatchAsync_BatchSizeZeroOrNegative_Throws(int invalidBatchSize)
    {
        var store = new InMemoryTimeoutStore();

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.GetTimeoutsBatchAsync(batchSize: invalidBatchSize));

        Assert.Equal("batchSize", ex.ParamName);
    }
}
