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
}
