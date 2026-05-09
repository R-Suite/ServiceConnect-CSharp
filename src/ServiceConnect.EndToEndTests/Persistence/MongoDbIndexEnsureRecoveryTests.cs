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

    // Named type implementing IHasCorrelationId — required since anonymous types cannot
    // implement interfaces and InsertDataAsync now enforces the contract.
    private sealed class IndexRecoveryItem : IHasCorrelationId
    {
        public Guid CorrelationId { get; set; }
        public string Value { get; set; } = "";
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task TimeoutStore_AfterDbDrop_DoesNotRecreateIndexes_ByDesign()
    {
        // H14 (Phase 4): EnsureTimeoutIndexAsync caches a per-instance _indexed flag
        // after first success, mirroring the saga finder and aggregator persistor. If
        // an admin drops the database while the process is still running, the cached
        // store will not re-create the indexes on the next insert — operators must
        // recycle the store (process restart) to recover. The trade-off vs the
        // per-message DropOneAsync + CreateManyAsync round-trip is documented; this
        // test pins the new contract so a future regression is caught.
        var dbName = _fixture.GetUniqueDatabaseName("idxrecovery_to");
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = dbName,
        };
        var client = MongoClientFactory.Create(options);
        var store = new MongoDbTimeoutStore(
            client, options, NullLogger<MongoDbTimeoutStore>.Instance, new FakeTimeProvider(DateTimeOffset.UtcNow));

        // First write: causes EnsureTimeoutIndexAsync to create the indexes and
        // flip _indexed=1.
        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = Guid.NewGuid(),
            Time = DateTimeOffset.UtcNow.AddMinutes(1),
        });

        var firstIndexes = await ListIndexNamesAsync(client, dbName, "Timeouts");
        Assert.Contains("Time_1_Locked_1", firstIndexes);

        // Simulate an admin dropping the database while the process is still running.
        await client.DropDatabaseAsync(dbName);

        // Second write on the same store instance: the cache flag short-circuits the
        // ensure path, so the indexes are NOT recreated. Pins the H14 contract.
        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = Guid.NewGuid(),
            Time = DateTimeOffset.UtcNow.AddMinutes(1),
        });

        var secondIndexes = await ListIndexNamesAsync(client, dbName, "Timeouts");
        Assert.DoesNotContain("Time_1_Locked_1", secondIndexes);
        Assert.DoesNotContain("LockedBy_1_Locked_1", secondIndexes);
        Assert.DoesNotContain("LockExpiresAt_1", secondIndexes);
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

        registry.Register(typeof(IndexRecoveryItem));
        var item = new IndexRecoveryItem { Value = "first", CorrelationId = Guid.NewGuid() };

        // First write: causes EnsureIndexesAsync to create the indexes and flip _indexed=1.
        await persistor.InsertDataAsync(item, "batch1");

        var firstIndexes = await ListIndexNamesAsync(client, dbName, collectionName);
        Assert.Contains("Name_1", firstIndexes);

        // Simulate an admin dropping the database while the process is still running.
        await client.DropDatabaseAsync(dbName);

        // Second write on the same persistor instance: the cache flag short-circuits
        // the ensure path, so the indexes are NOT recreated. The test pins this contract.
        var item2 = new IndexRecoveryItem { Value = "second", CorrelationId = Guid.NewGuid() };
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
