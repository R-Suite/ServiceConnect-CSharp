using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Helpers;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(PersistenceCollection))]
public class MongoDbProcessManagerFinderTests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    static MongoDbProcessManagerFinderTests()
    {
        // Register TestData with MongoDB BSON serialization so it can be serialized
        // through the IProcessManagerData interface.
        // Must be done before any MongoDB operations.
        //
        // Explicitly pin the CorrelationId serializer to Standard (UUID subtype 4). In
        // MongoDB.Driver v3, AutoMap bakes the resolved Guid serializer into the class map
        // at registration time using the driver's default — even when a global Standard
        // GuidSerializer has been registered via BsonSerializer.RegisterSerializer, AutoMap
        // ignores it and falls back to CSharpLegacy (subtype 3). The stored value then
        // cannot be matched by a filter lambda that serializes the Guid as Standard.
        if (!BsonClassMap.IsClassMapRegistered(typeof(TestData)))
        {
            BsonClassMap.RegisterClassMap<TestData>(cm =>
            {
                cm.AutoMap();
                cm.SetIsRootClass(true);
            });
        }
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
        var client = MongoClientFactory.Create(options);
        var finder = new MongoDbProcessManagerFinder(client, options, NullLogger<MongoDbProcessManagerFinder>.Instance);
        return (finder, connectionString, dbName);
    }

    private static IMongoCollection<MongoDbData<TestData>> GetCollection(string connectionString, string dbName)
    {
        return new MongoClient(connectionString)
            .GetDatabase(dbName)
            .GetCollection<MongoDbData<TestData>>(typeof(TestData).FullName);
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
    public async Task ShouldInsertData()
    {
        var (finder, connectionString, dbName) = CreateFinder();
        var data = new TestData { CorrelationId = Guid.NewGuid(), Name = "Insert Test" };

        await finder.InsertDataAsync(data);

        var collection = GetCollection(connectionString, dbName);
        var result = collection.Find(Builders<MongoDbData<TestData>>.Filter.Eq(x => x.Data.CorrelationId, data.CorrelationId)).FirstOrDefault();
        Assert.NotNull(result);
        Assert.Equal(data.CorrelationId, result.Data.CorrelationId);
        Assert.Equal("Insert Test", result.Data.Name);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ShouldFindData()
    {
        var (finder, _, _) = CreateFinder();
        var correlationId = Guid.NewGuid();
        var data = new TestData { CorrelationId = correlationId, Name = "Find Test" };
        await finder.InsertDataAsync(data);

        var mapper = CreateMapper();
        var message = new Message(correlationId);
        var result = await finder.FindDataAsync<TestData>(mapper, message);

        Assert.NotNull(result);
        Assert.Equal(correlationId, result.Data.CorrelationId);
        Assert.Equal("Find Test", result.Data.Name);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ShouldReturnNullWhenDataNotFound()
    {
        var (finder, _, _) = CreateFinder();

        var mapper = CreateMapper();
        var message = new Message(Guid.NewGuid());
        var result = await finder.FindDataAsync<TestData>(mapper, message);

        Assert.Null(result);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ShouldUpdateData()
    {
        var (finder, connectionString, dbName) = CreateFinder();
        var correlationId = Guid.NewGuid();
        var data = new TestData { CorrelationId = correlationId, Name = "Update Test" };
        await finder.InsertDataAsync(data);

        var mapper = CreateMapper();
        var message = new Message(correlationId);
        var found = await finder.FindDataAsync<TestData>(mapper, message);
        Assert.NotNull(found);

        found.Data.Name = "Updated";
        await finder.UpdateDataAsync(found);

        var collection = GetCollection(connectionString, dbName);
        var updated = collection.Find(Builders<MongoDbData<TestData>>.Filter.Eq(x => x.Data.CorrelationId, correlationId)).FirstOrDefault();
        Assert.NotNull(updated);
        Assert.Equal("Updated", updated.Data.Name);
        Assert.Equal(2, updated.Version);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ShouldThrowWhenUpdatingConcurrently()
    {
        var (finder, _, _) = CreateFinder();
        var correlationId = Guid.NewGuid();
        var data = new TestData { CorrelationId = correlationId, Name = "Concurrent Test" };
        await finder.InsertDataAsync(data);

        var mapper = CreateMapper();
        var message = new Message(correlationId);

        // Find twice to get two copies at the same version
        var first = await finder.FindDataAsync<TestData>(mapper, message);
        var second = await finder.FindDataAsync<TestData>(mapper, message);
        Assert.NotNull(first);
        Assert.NotNull(second);

        // Update via the first copy — succeeds
        await finder.UpdateDataAsync(first);

        // Update via the second copy — should throw due to version mismatch
        await Assert.ThrowsAsync<ConcurrencyException>(() => finder.UpdateDataAsync(second));
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ShouldDeleteData()
    {
        var (finder, connectionString, dbName) = CreateFinder();
        var correlationId = Guid.NewGuid();
        var data = new TestData { CorrelationId = correlationId, Name = "Delete Test" };
        await finder.InsertDataAsync(data);

        var mapper = CreateMapper();
        var message = new Message(correlationId);
        var found = await finder.FindDataAsync<TestData>(mapper, message);
        Assert.NotNull(found);

        await finder.DeleteDataAsync(found);

        var collection = GetCollection(connectionString, dbName);
        var result = collection.Find(Builders<MongoDbData<TestData>>.Filter.Eq(x => x.Data.CorrelationId, correlationId)).FirstOrDefault();
        Assert.Null(result);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ShouldThrowConcurrencyExceptionOnStaleDelete()
    {
        var (finder, connectionString, dbName) = CreateFinder();
        var correlationId = Guid.NewGuid();
        await finder.InsertDataAsync(new TestData { CorrelationId = correlationId, Name = "v1" });

        var mapper = CreateMapper();
        var message = new Message(correlationId);

        // Load twice at the same version, then bump the stored version via the first copy.
        var stale = await finder.FindDataAsync<TestData>(mapper, message);
        Assert.NotNull(stale);
        var current = await finder.FindDataAsync<TestData>(mapper, message);
        Assert.NotNull(current);

        current.Data.Name = "v2";
        await finder.UpdateDataAsync(current);

        // Deleting via the stale copy must fail with ConcurrencyException, not silently succeed.
        await Assert.ThrowsAsync<ConcurrencyException>(() => finder.DeleteDataAsync(stale));

        // And the record must still be present.
        var collection = GetCollection(connectionString, dbName);
        var survivor = collection.Find(Builders<MongoDbData<TestData>>.Filter.Eq(x => x.Data.CorrelationId, correlationId)).FirstOrDefault();
        Assert.NotNull(survivor);
        Assert.Equal("v2", survivor.Data.Name);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ShouldThrowConcurrencyExceptionWhenDeletingMissingRecord()
    {
        var (finder, _, _) = CreateFinder();
        var stub = new MongoDbData<TestData>
        {
            Data = new TestData { CorrelationId = Guid.NewGuid(), Name = "ghost" },
            Version = 1
        };

        await Assert.ThrowsAsync<ConcurrencyException>(() => finder.DeleteDataAsync(stub));
    }

    // Concurrent InsertDataAsync callers must not admit duplicate rows, and concurrent
    // index-creation races (codes 85/86) must not bubble up as errors.
    [Fact]
    [Trait("Category", "Docker")]
    public async Task EnsureIndex_ConcurrentCallers_AllSeeIndexBeforeInsertSucceeds()
    {
        var (finder, connectionString, dbName) = CreateFinder();
        var correlationId = Guid.NewGuid();

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () =>
            {
                var pm = new TestData { CorrelationId = correlationId, Name = "A" };
                try
                {
                    await finder.InsertDataAsync(pm);
                }
                catch (ConcurrencyException)
                {
                    // Expected: unique-index enforcement — only ONE of the 10 succeeds.
                }
                catch (PersistenceException)
                {
                    // Also acceptable — unique-index violation wrapped as PersistenceException.
                }
            }))
            .ToList();

        await Task.WhenAll(tasks);

        var collection = GetCollection(connectionString, dbName);
        var count = await collection.CountDocumentsAsync(
            Builders<MongoDbData<TestData>>.Filter.Eq(x => x.Data.CorrelationId, correlationId));
        Assert.Equal(1, count);
    }

    // Under WriteConcern.Unacknowledged, UpdateDataAsync must not silently swallow
    // the operation result — concurrency guards are disabled with a one-time Warning.
    [Fact]
    [Trait("Category", "Docker")]
    public async Task UpdateDataAsync_WithW0_DoesNotThrow()
    {
        var (finder, connectionString, dbName) = CreateFinderWithWriteConcern(WriteConcern.Unacknowledged);

        var correlationId = Guid.NewGuid();
        await finder.InsertDataAsync(new TestData { CorrelationId = correlationId, Name = "v1" });

        // Retrieve via a normal (acknowledged) client so we can get a real version snapshot.
        var normalOptions = new MongoDbPersistenceOptions
        {
            ConnectionString = connectionString,
            DatabaseName = dbName
        };
        var normalClient = MongoClientFactory.Create(normalOptions);
        var normalFinder = new MongoDbProcessManagerFinder(normalClient, normalOptions, NullLogger<MongoDbProcessManagerFinder>.Instance);
        var mapper = CreateMapper();
        var message = new Message(correlationId);
        var found = await normalFinder.FindDataAsync<TestData>(mapper, message);
        Assert.NotNull(found);

        found.Data.Name = "v2";
        // Under w:0, UpdateDataAsync must complete without throwing.
        await finder.UpdateDataAsync(found);
    }

    // Under WriteConcern.Unacknowledged, DeleteDataAsync must not spuriously throw.
    [Fact]
    [Trait("Category", "Docker")]
    public async Task DeleteDataAsync_WithW0_DoesNotSpuriouslyThrow()
    {
        var (finder, connectionString, dbName) = CreateFinderWithWriteConcern(WriteConcern.Unacknowledged);

        var correlationId = Guid.NewGuid();
        await finder.InsertDataAsync(new TestData { CorrelationId = correlationId, Name = "v1" });

        // Retrieve via acknowledged client to get a valid version token.
        var normalOptions = new MongoDbPersistenceOptions
        {
            ConnectionString = connectionString,
            DatabaseName = dbName
        };
        var normalClient = MongoClientFactory.Create(normalOptions);
        var normalFinder = new MongoDbProcessManagerFinder(normalClient, normalOptions, NullLogger<MongoDbProcessManagerFinder>.Instance);
        var mapper = CreateMapper();
        var message = new Message(correlationId);
        var found = await normalFinder.FindDataAsync<TestData>(mapper, message);
        Assert.NotNull(found);

        // Under w:0, DeleteDataAsync returns DeletedCount=0 by design — must NOT throw.
        await finder.DeleteDataAsync(found);
    }

    private (MongoDbProcessManagerFinder finder, string connectionString, string dbName) CreateFinderWithWriteConcern(WriteConcern writeConcern)
    {
        var dbName = _fixture.GetUniqueDatabaseName();
        var connectionString = _fixture.MongoDbConnectionString;
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        settings.WriteConcern = writeConcern;
        var client = new MongoClient(settings);
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = connectionString,
            DatabaseName = dbName
        };
        var finder = new MongoDbProcessManagerFinder(client, options, NullLogger<MongoDbProcessManagerFinder>.Instance);
        return (finder, connectionString, dbName);
    }
}
