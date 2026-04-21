using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Persistence.MongoDb;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(PersistenceCollection))]
public class MongoDbAggregatorPersistorTests
{
    private readonly PersistenceFixture _fixture;

    public MongoDbAggregatorPersistorTests(PersistenceFixture fixture)
    {
        _fixture = fixture;
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
        var item1 = new { Value = "item1", CorrelationId = correlationId1 };
        var item2 = new { Value = "item2", CorrelationId = correlationId2 };

        var registry = new MessageTypeRegistry();
        registry.Register(item1.GetType());

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

        await persistor.InsertDataAsync(new { Value = "item1", CorrelationId = correlationId1 }, "batch2");
        await persistor.InsertDataAsync(new { Value = "item2", CorrelationId = correlationId2 }, "batch2");

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

        await persistor.InsertDataAsync(new { Value = "item1", CorrelationId = correlationId1 }, "batch3");
        await persistor.InsertDataAsync(new { Value = "item2", CorrelationId = correlationId2 }, "batch3");

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
        // M17 regression: snapshots now sort by InsertedAtTicks so the
        // aggregator handler sees messages in the order they were written,
        // not whatever order the Mongo cursor returns. We drive a fake
        // TimeProvider forward between inserts so each row has a distinct
        // monotonic tick value.
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

        var names = new[] { "first", "second", "third", "fourth", "fifth" };
        foreach (var name in names)
        {
            var item = new { CorrelationId = Guid.NewGuid(), Label = name };
            registry.Register(item.GetType());
            await persistor.InsertDataAsync(item, "ordered");
            time.Advance(TimeSpan.FromMilliseconds(25));
        }

        var result = await persistor.GetDataAsync("ordered");

        var labels = result.Select(o => (string)o.GetType().GetProperty("Label")!.GetValue(o)!).ToArray();
        Assert.Equal(names, labels);
    }
}
