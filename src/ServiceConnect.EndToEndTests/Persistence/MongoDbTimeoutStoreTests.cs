using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(PersistenceCollection))]
public class MongoDbTimeoutStoreTests
{
    private readonly PersistenceFixture _fixture;

    public MongoDbTimeoutStoreTests(PersistenceFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task RemoveDispatchedTimeoutAsync_LeaseAware_ThrowsWhenLeaseIsStale()
    {
        // Regression guard: a caller that fell asleep and lost its lease (reaper reassigned
        // to another worker) must observe the invalidation when it tries to Remove with its
        // stale sessionId. Without the fix, DeleteOneAsync with a non-matching filter returns
        // DeletedCount=0 silently and the caller assumes success — leaving a pending timeout
        // behind that another worker now owns.
        var store = BuildStore("leasestale_rm", out var client, out var dbName, out _);

        var timeoutId = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = timeoutId,
            Time = new DateTimeOffset(2026, 4, 22, 11, 55, 0, TimeSpan.Zero),
        });

        // Claim the row — sessionA now holds the lease.
        var batch = await store.GetTimeoutsBatchAsync();
        var claimed = Assert.Single(batch.DueTimeouts);
        var sessionA = claimed.LockedBy;
        Assert.NotEqual(Guid.Empty, sessionA);

        // Simulate lease reassignment to sessionB via a direct collection write.
        var collection = client.GetDatabase(dbName).GetCollection<TimeoutData>("Timeouts");
        var sessionB = Guid.NewGuid();
        var reassignResult = await collection.UpdateOneAsync(
            Builders<TimeoutData>.Filter.Eq(x => x.Id, timeoutId),
            Builders<TimeoutData>.Update.Set(x => x.LockedBy, sessionB));
        Assert.Equal(1, reassignResult.MatchedCount);

        // sessionA tries to Remove with its now-stale lockOwner. Expect ConcurrencyException.
        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            ((ILeaseAwareTimeoutStore)store).RemoveDispatchedTimeoutAsync(timeoutId, sessionA));

        // And the row must still be present so the rightful owner can still dispatch it.
        var surviving = await collection.Find(Builders<TimeoutData>.Filter.Eq(x => x.Id, timeoutId))
            .FirstOrDefaultAsync();
        Assert.NotNull(surviving);
        Assert.Equal(sessionB, surviving.LockedBy);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ReleaseDispatchedTimeoutAsync_LeaseAware_ThrowsWhenLeaseIsStale()
    {
        // Same setup as the Remove test but exercising the UpdateOneAsync / MatchedCount
        // path. A caller whose lease was reassigned between read and Release must see the
        // invalidation rather than quietly clearing Locked/LockedBy (which would cause a
        // duplicate dispatch by the worker that now owns the lease).
        var store = BuildStore("leasestale_rel", out var client, out var dbName, out _);

        var timeoutId = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = timeoutId,
            Time = new DateTimeOffset(2026, 4, 22, 11, 55, 0, TimeSpan.Zero),
        });

        var batch = await store.GetTimeoutsBatchAsync();
        var claimed = Assert.Single(batch.DueTimeouts);
        var sessionA = claimed.LockedBy;

        var collection = client.GetDatabase(dbName).GetCollection<TimeoutData>("Timeouts");
        var sessionB = Guid.NewGuid();
        await collection.UpdateOneAsync(
            Builders<TimeoutData>.Filter.Eq(x => x.Id, timeoutId),
            Builders<TimeoutData>.Update.Set(x => x.LockedBy, sessionB));

        await Assert.ThrowsAsync<ConcurrencyException>(() =>
            ((ILeaseAwareTimeoutStore)store).ReleaseDispatchedTimeoutAsync(timeoutId, sessionA));

        // LockedBy must still be sessionB; Release from a stale owner must not clear the lease.
        var surviving = await collection.Find(Builders<TimeoutData>.Filter.Eq(x => x.Id, timeoutId))
            .FirstOrDefaultAsync();
        Assert.NotNull(surviving);
        Assert.True(surviving.Locked);
        Assert.Equal(sessionB, surviving.LockedBy);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task RemoveDispatchedTimeoutAsync_LeaseAware_SucceedsForCorrectOwner()
    {
        // Happy path: the session that actually holds the lease can still Remove without
        // incident and the row is gone afterwards.
        var store = BuildStore("leaseok_rm", out var client, out var dbName, out _);

        var timeoutId = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = timeoutId,
            Time = new DateTimeOffset(2026, 4, 22, 11, 55, 0, TimeSpan.Zero),
        });

        var batch = await store.GetTimeoutsBatchAsync();
        var claimed = Assert.Single(batch.DueTimeouts);

        await ((ILeaseAwareTimeoutStore)store)
            .RemoveDispatchedTimeoutAsync(timeoutId, claimed.LockedBy);

        var collection = client.GetDatabase(dbName).GetCollection<TimeoutData>("Timeouts");
        var remaining = await collection.Find(Builders<TimeoutData>.Filter.Eq(x => x.Id, timeoutId))
            .FirstOrDefaultAsync();
        Assert.Null(remaining);
    }

    private MongoDbTimeoutStore BuildStore(
        string dbPrefix,
        out IMongoClient client,
        out string dbName,
        out FakeTimeProvider timeProvider)
    {
        var now = new DateTimeOffset(2026, 4, 22, 12, 0, 0, TimeSpan.Zero);
        timeProvider = new FakeTimeProvider(now);
        dbName = _fixture.GetUniqueDatabaseName(dbPrefix);
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = dbName,
        };
        client = MongoClientFactory.Create(options);
        return new MongoDbTimeoutStore(
            client,
            options,
            NullLogger<MongoDbTimeoutStore>.Instance,
            timeProvider);
    }
}
