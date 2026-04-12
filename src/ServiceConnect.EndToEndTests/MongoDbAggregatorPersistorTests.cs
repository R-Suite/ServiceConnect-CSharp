using Microsoft.Extensions.Logging.Abstractions;
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
        return new MongoDbAggregatorPersistor(options, collectionName, NullLogger<MongoDbAggregatorPersistor>.Instance, registry ?? new MessageTypeRegistry());
    }

    [Fact]
    [Trait("Category", "Docker")]
    public void InsertData_AndGetData_ReturnsInsertedItems()
    {
        var correlationId1 = Guid.NewGuid();
        var correlationId2 = Guid.NewGuid();
        var item1 = new { Value = "item1", CorrelationId = correlationId1 };
        var item2 = new { Value = "item2", CorrelationId = correlationId2 };

        var registry = new MessageTypeRegistry();
        registry.Register(item1.GetType());

        var persistor = CreatePersistor(registry: registry);

        persistor.InsertData(item1, "batch1");
        persistor.InsertData(item2, "batch1");

        var result = persistor.GetData("batch1");

        Assert.Equal(2, result.Count);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public void Count_ReturnsCorrectCount()
    {
        var persistor = CreatePersistor();
        var correlationId1 = Guid.NewGuid();
        var correlationId2 = Guid.NewGuid();

        persistor.InsertData(new { Value = "item1", CorrelationId = correlationId1 }, "batch2");
        persistor.InsertData(new { Value = "item2", CorrelationId = correlationId2 }, "batch2");

        var count = persistor.Count("batch2");

        Assert.Equal(2, count);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public void RemoveData_RemovesByCorrelationId()
    {
        var persistor = CreatePersistor();
        var correlationId1 = Guid.NewGuid();
        var correlationId2 = Guid.NewGuid();

        persistor.InsertData(new { Value = "item1", CorrelationId = correlationId1 }, "batch3");
        persistor.InsertData(new { Value = "item2", CorrelationId = correlationId2 }, "batch3");

        persistor.RemoveData("batch3", correlationId1);

        var count = persistor.Count("batch3");
        Assert.Equal(1, count);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public void GetData_ReturnsEmptyList_WhenNoData()
    {
        var persistor = CreatePersistor();

        var result = persistor.GetData("nonexistent");

        Assert.Empty(result);
    }
}
