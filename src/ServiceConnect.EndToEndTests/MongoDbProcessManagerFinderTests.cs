using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Helpers;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(PersistenceCollection))]
public class MongoDbProcessManagerFinderTests
{
    private readonly PersistenceFixture _fixture;

    static MongoDbProcessManagerFinderTests()
    {
        // Register TestData with MongoDB BSON serialization so it can be serialized
        // through the IProcessManagerData interface.
        // Must be done before any MongoDB operations.
        if (!BsonClassMap.IsClassMapRegistered(typeof(TestData)))
        {
            BsonClassMap.RegisterClassMap<TestData>(cm =>
            {
                cm.AutoMap();
                cm.SetIsRootClass(true);
            });
        }
    }

    public MongoDbProcessManagerFinderTests(PersistenceFixture fixture)
    {
        _fixture = fixture;
    }

    private (MongoDbProcessManagerFinder finder, string connectionString, string dbName) CreateFinder()
    {
        var dbName = _fixture.GetUniqueDatabaseName();
        var connectionString = _fixture.MongoDbConnectionString;
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = connectionString,
            DatabaseName = dbName
        };
        var finder = new MongoDbProcessManagerFinder(options, NullLogger<MongoDbProcessManagerFinder>.Instance);
        return (finder, connectionString, dbName);
    }

    private static IMongoCollection<MongoDbData<TestData>> GetCollection(string connectionString, string dbName)
    {
        return new MongoClient(connectionString)
            .GetDatabase(dbName)
            .GetCollection<MongoDbData<TestData>>("TestData");
    }

    private static TestProcessManagerPropertyMapper CreateMapper()
    {
        var mapper = new TestProcessManagerPropertyMapper();
        mapper.ConfigureMapping<IProcessManagerData, Message>(
            m => m.CorrelationId,
            pm => pm.CorrelationId);
        return mapper;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public void ShouldInsertData()
    {
        var (finder, connectionString, dbName) = CreateFinder();
        var data = new TestData { CorrelationId = Guid.NewGuid(), Name = "Insert Test" };

        finder.InsertData(data);

        var collection = GetCollection(connectionString, dbName);
        var result = collection.Find(Builders<MongoDbData<TestData>>.Filter.Eq(x => x.Data.CorrelationId, data.CorrelationId)).FirstOrDefault();
        Assert.NotNull(result);
        Assert.Equal(data.CorrelationId, result.Data.CorrelationId);
        Assert.Equal("Insert Test", result.Data.Name);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public void ShouldFindData()
    {
        var (finder, _, _) = CreateFinder();
        var correlationId = Guid.NewGuid();
        var data = new TestData { CorrelationId = correlationId, Name = "Find Test" };
        finder.InsertData(data);

        var mapper = CreateMapper();
        var message = new Message(correlationId);
        var result = finder.FindData<TestData>(mapper, message);

        Assert.NotNull(result);
        Assert.Equal(correlationId, result.Data.CorrelationId);
        Assert.Equal("Find Test", result.Data.Name);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public void ShouldReturnNullWhenDataNotFound()
    {
        var (finder, _, _) = CreateFinder();

        var mapper = CreateMapper();
        var message = new Message(Guid.NewGuid());
        var result = finder.FindData<TestData>(mapper, message);

        Assert.Null(result);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public void ShouldUpdateData()
    {
        var (finder, connectionString, dbName) = CreateFinder();
        var correlationId = Guid.NewGuid();
        var data = new TestData { CorrelationId = correlationId, Name = "Update Test" };
        finder.InsertData(data);

        var mapper = CreateMapper();
        var message = new Message(correlationId);
        var found = finder.FindData<TestData>(mapper, message);
        Assert.NotNull(found);

        found.Data.Name = "Updated";
        finder.UpdateData(found);

        var collection = GetCollection(connectionString, dbName);
        var updated = collection.Find(Builders<MongoDbData<TestData>>.Filter.Eq(x => x.Data.CorrelationId, correlationId)).FirstOrDefault();
        Assert.NotNull(updated);
        Assert.Equal("Updated", updated.Data.Name);
        Assert.Equal(2, updated.Version);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public void ShouldThrowWhenUpdatingConcurrently()
    {
        var (finder, _, _) = CreateFinder();
        var correlationId = Guid.NewGuid();
        var data = new TestData { CorrelationId = correlationId, Name = "Concurrent Test" };
        finder.InsertData(data);

        var mapper = CreateMapper();
        var message = new Message(correlationId);

        // Find twice to get two copies at the same version
        var first = finder.FindData<TestData>(mapper, message);
        var second = finder.FindData<TestData>(mapper, message);
        Assert.NotNull(first);
        Assert.NotNull(second);

        // Update via the first copy — succeeds
        finder.UpdateData(first);

        // Update via the second copy — should throw due to version mismatch
        Assert.Throws<ArgumentException>(() => finder.UpdateData(second));
    }

    [Fact]
    [Trait("Category", "Docker")]
    public void ShouldDeleteData()
    {
        var (finder, connectionString, dbName) = CreateFinder();
        var correlationId = Guid.NewGuid();
        var data = new TestData { CorrelationId = correlationId, Name = "Delete Test" };
        finder.InsertData(data);

        var mapper = CreateMapper();
        var message = new Message(correlationId);
        var found = finder.FindData<TestData>(mapper, message);
        Assert.NotNull(found);

        finder.DeleteData(found);

        var collection = GetCollection(connectionString, dbName);
        var result = collection.Find(Builders<MongoDbData<TestData>>.Filter.Eq(x => x.Data.CorrelationId, correlationId)).FirstOrDefault();
        Assert.Null(result);
    }
}
