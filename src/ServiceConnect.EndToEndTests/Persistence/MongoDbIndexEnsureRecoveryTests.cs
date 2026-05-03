using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(PersistenceCollection))]
public class MongoDbIndexEnsureRecoveryTests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task TimeoutStore_AfterDbDrop_RecreatesIndexesOnNextInsert()
    {
        var dbName = _fixture.GetUniqueDatabaseName("idxrecovery_to");
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = dbName,
        };
        var client = MongoClientFactory.Create(options);
        var store = new MongoDbTimeoutStore(
            client, options, NullLogger<MongoDbTimeoutStore>.Instance, new FakeTimeProvider(DateTimeOffset.UtcNow));

        // First write: causes EnsureTimeoutIndexAsync to create the indexes.
        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = Guid.NewGuid(),
            Time = DateTimeOffset.UtcNow.AddMinutes(1),
        });

        var firstIndexes = await ListIndexNamesAsync(client, dbName, "Timeouts");
        Assert.Contains("Time_1_Locked_1", firstIndexes);

        // Simulate an admin dropping the database while the process is still running.
        await client.DropDatabaseAsync(dbName);

        // Second write on the same store instance: without a cache flag the ensure path
        // runs again, hitting Mongo unconditionally and recreating the indexes.
        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = Guid.NewGuid(),
            Time = DateTimeOffset.UtcNow.AddMinutes(1),
        });

        var secondIndexes = await ListIndexNamesAsync(client, dbName, "Timeouts");
        Assert.Contains("Time_1_Locked_1", secondIndexes);
        Assert.Contains("LockedBy_1_Locked_1", secondIndexes);
        Assert.Contains("LockExpiresAt_1", secondIndexes);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task AggregatorPersistor_AfterDbDrop_DoesNotRecreateIndexes_ByDesign()
    {
        // M38 (Phase 9): EnsureIndexesAsync caches a per-instance _indexed flag after
        // first success. If an admin drops the database while the process is still
        // running, the cached persistor will not re-create the indexes on the next
        // insert — operators must recycle the persistor (process restart) to recover.
        // The trade-off vs the per-message round-trip pre-Phase-9 is documented; this
        // test pins the new contract so a future regression is caught.
        var dbName = _fixture.GetUniqueDatabaseName("idxrecovery_agg");
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = dbName,
        };
        var client = MongoClientFactory.Create(options);
        var registry = new MessageTypeRegistry();
        var collectionName = "TestAggregator";
        var persistor = new MongoDbAggregatorPersistor(
            client, options, collectionName, NullLogger<MongoDbAggregatorPersistor>.Instance, registry);

        var item = new { Value = "first", CorrelationId = Guid.NewGuid() };
        registry.Register(item.GetType());

        // First write: causes EnsureIndexesAsync to create the indexes and flip _indexed=1.
        await persistor.InsertDataAsync(item, "batch1");

        var firstIndexes = await ListIndexNamesAsync(client, dbName, collectionName);
        Assert.Contains("Name_1", firstIndexes);

        // Simulate an admin dropping the database while the process is still running.
        await client.DropDatabaseAsync(dbName);

        // Second write on the same persistor instance: the cache flag short-circuits
        // the ensure path, so the indexes are NOT recreated. The test pins this contract.
        var item2 = new { Value = "second", CorrelationId = Guid.NewGuid() };
        await persistor.InsertDataAsync(item2, "batch1");

        var secondIndexes = await ListIndexNamesAsync(client, dbName, collectionName);
        Assert.DoesNotContain("Name_1", secondIndexes);
    }

    private static async Task<List<string>> ListIndexNamesAsync(IMongoClient client, string db, string coll)
    {
        var cursor = await client.GetDatabase(db)
            .GetCollection<BsonDocument>(coll)
            .Indexes.ListAsync();
        var indexes = await cursor.ToListAsync();
        return [.. indexes.Select(idx => idx["name"].AsString)];
    }
}
