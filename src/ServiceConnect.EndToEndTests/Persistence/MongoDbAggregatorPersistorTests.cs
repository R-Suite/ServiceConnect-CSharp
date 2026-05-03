using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.MongoDb;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(PersistenceCollection))]
public class MongoDbAggregatorPersistorTests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    // Named types implementing IHasCorrelationId — required since anonymous types
    // cannot implement interfaces and InsertDataAsync now enforces the contract.
    private sealed class AggregatorTestItem : IHasCorrelationId
    {
        public Guid CorrelationId { get; set; }
        public string Value { get; set; } = "";
    }

    private sealed class AggregatorLabelItem : IHasCorrelationId
    {
        public Guid CorrelationId { get; set; }
        public string Label { get; set; } = "";
    }

    private MongoDbAggregatorPersistor CreatePersistor(string collectionName = "TestAggregator", MessageTypeRegistry? registry = null)
    {
        var dbName = _fixture.GetUniqueDatabaseName();
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = dbName
        };
        var client = MongoClientFactory.Create(options);
        return new MongoDbAggregatorPersistor(client, options, collectionName, NullLogger<MongoDbAggregatorPersistor>.Instance, registry ?? new MessageTypeRegistry());
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task InsertData_AndGetData_ReturnsInsertedItems()
    {
        var correlationId1 = Guid.NewGuid();
        var correlationId2 = Guid.NewGuid();
        var item1 = new AggregatorTestItem { Value = "item1", CorrelationId = correlationId1 };
        var item2 = new AggregatorTestItem { Value = "item2", CorrelationId = correlationId2 };

        var registry = new MessageTypeRegistry();
        registry.Register(typeof(AggregatorTestItem));

        var persistor = CreatePersistor(registry: registry);

        await persistor.InsertDataAsync(item1, "batch1");
        await persistor.InsertDataAsync(item2, "batch1");

        var result = await persistor.GetDataAsync("batch1");

        Assert.Equal(2, result.Count);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Count_ReturnsCorrectCount()
    {
        var persistor = CreatePersistor();
        var correlationId1 = Guid.NewGuid();
        var correlationId2 = Guid.NewGuid();

        await persistor.InsertDataAsync(new AggregatorTestItem { Value = "item1", CorrelationId = correlationId1 }, "batch2");
        await persistor.InsertDataAsync(new AggregatorTestItem { Value = "item2", CorrelationId = correlationId2 }, "batch2");

        var count = await persistor.CountAsync("batch2");

        Assert.Equal(2, count);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task RemoveData_RemovesByCorrelationId()
    {
        var persistor = CreatePersistor();
        var correlationId1 = Guid.NewGuid();
        var correlationId2 = Guid.NewGuid();

        await persistor.InsertDataAsync(new AggregatorTestItem { Value = "item1", CorrelationId = correlationId1 }, "batch3");
        await persistor.InsertDataAsync(new AggregatorTestItem { Value = "item2", CorrelationId = correlationId2 }, "batch3");

        await persistor.RemoveDataAsync("batch3", correlationId1);

        var count = await persistor.CountAsync("batch3");
        Assert.Equal(1, count);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task GetData_ReturnsEmptyList_WhenNoData()
    {
        var persistor = CreatePersistor();

        var result = await persistor.GetDataAsync("nonexistent");

        Assert.Empty(result);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task GetData_ReturnsMessagesInInsertionOrder()
    {
        // Snapshots must sort by InsertedAtTicks so the aggregator handler sees
        // messages in the order they were written rather than whatever order the
        // Mongo cursor happens to return. The fake TimeProvider is advanced
        // between inserts so each row carries a distinct monotonic tick value.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero));
        var dbName = _fixture.GetUniqueDatabaseName();
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = dbName,
        };
        var client = MongoClientFactory.Create(options);
        var registry = new MessageTypeRegistry();
        var persistor = new MongoDbAggregatorPersistor(
            client, options, "OrderedAggregator",
            NullLogger<MongoDbAggregatorPersistor>.Instance, registry, time);

        registry.Register(typeof(AggregatorLabelItem));
        var names = new[] { "first", "second", "third", "fourth", "fifth" };
        foreach (var name in names)
        {
            var item = new AggregatorLabelItem { CorrelationId = Guid.NewGuid(), Label = name };
            await persistor.InsertDataAsync(item, "ordered");
            time.Advance(TimeSpan.FromMilliseconds(25));
        }

        var result = await persistor.GetDataAsync("ordered");

        var labels = result.Cast<AggregatorLabelItem>().Select(o => o.Label).ToArray();
        Assert.Equal(names, labels);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task EnsureIndexes_CancellationTokenCanceled_ThrowsOperationCanceled()
    {
        // Index creation must observe the CancellationToken so a shutting-down host can
        // interrupt a stalled CreateManyAsync rather than blocking indefinitely. Pre-cancel
        // the token and assert the first write fails fast with OperationCanceledException.
        var persistor = CreatePersistor();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var registry = new MessageTypeRegistry();
        registry.Register(typeof(AggregatorTestItem));
        var item = new AggregatorTestItem { CorrelationId = Guid.NewGuid(), Value = "test" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => persistor.InsertDataAsync(item, "l9-batch", cts.Token));
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task EnsureIndexes_ConcurrentProcessCreatedSameIndex_DoesNotThrow()
    {
        // Benign MongoCommandException 85/86 from concurrent index creation must not
        // propagate as a persistence failure. Pre-create the compound index with a different
        // Name so the persistor's CreateManyAsync hits Code 85 (IndexOptionsConflict).
        var dbName = _fixture.GetUniqueDatabaseName();
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = dbName,
        };
        var client = MongoClientFactory.Create(options);
        var database = client.GetDatabase(options.DatabaseName);
        var collection = database.GetCollection<BsonDocument>("TestAggregatorConflict");

        var conflictingKeys = Builders<BsonDocument>.IndexKeys
            .Ascending("Name")
            .Ascending("DataBson.CorrelationId");
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            conflictingKeys,
            new CreateIndexOptions { Name = "conflicting_name_correlation" }));

        var registry = new MessageTypeRegistry();
        registry.Register(typeof(AggregatorTestItem));
        var item = new AggregatorTestItem { CorrelationId = Guid.NewGuid(), Value = "test" };
        var persistor = new MongoDbAggregatorPersistor(
            client, options, "TestAggregatorConflict",
            NullLogger<MongoDbAggregatorPersistor>.Instance, registry);

        // First write triggers EnsureIndexesAsync; must NOT throw despite the conflict.
        var ex = await Record.ExceptionAsync(() => persistor.InsertDataAsync(item, "batch-conflict"));
        Assert.Null(ex);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task RemoveDataAsync_RowNotFound_ThrowsKeyNotFoundException()
    {
        // When no rows exist for the supplied Name, the delete is a structural mismatch —
        // the caller used the wrong aggregator name or all rows were already removed via
        // RemoveAllAsync. KeyNotFoundException signals "wrong name" more clearly than
        // ConcurrencyException (which is reserved for the concurrent-removal race).
        var persistor = CreatePersistor();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => persistor.RemoveDataAsync("nonexistent-agg", Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task RemoveDataAsync_NameExistsButCorrelationIdMismatch_ThrowsConcurrencyException()
    {
        // Companion to RowNotFound: the name bucket exists (so EnsureIndexes/collection isn't empty)
        // but no row carries the supplied correlationId. Silent no-op here would mask the same class
        // of data-integrity error the RowNotFound test guards against.
        var registry = new MessageTypeRegistry();
        registry.Register(typeof(AggregatorTestItem));
        var existing = new AggregatorTestItem { CorrelationId = Guid.NewGuid(), Value = "existing" };
        var persistor = CreatePersistor(registry: registry);

        await persistor.InsertDataAsync(existing, "batch-mismatch");

        await Assert.ThrowsAsync<ConcurrencyException>(
            () => persistor.RemoveDataAsync("batch-mismatch", Guid.NewGuid(), CancellationToken.None));
    }
}
