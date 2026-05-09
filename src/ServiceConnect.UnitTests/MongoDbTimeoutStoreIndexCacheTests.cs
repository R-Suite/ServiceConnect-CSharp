using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Verifies the per-instance index cache: after a successful first call, further
/// Insert / Get / Remove / Release / Reap operations skip both DropOneAsync and
/// CreateManyAsync round-trips. Mirrors MongoDbAggregatorPersistorIndexCacheTests
/// and the saga finder's _indexedCollections semantics.
/// </summary>
[Collection("Mongo Bson serial")]
public class MongoDbTimeoutStoreIndexCacheTests
{
    [Fact]
    public async Task EnsureTimeoutIndexAsync_AfterFirstCall_ShortCircuits()
    {
        // Drive 8 concurrent InsertTimeoutAsync; CreateManyAsync should fire exactly once.
        var indexManager = new Mock<IMongoIndexManager<TimeoutData>>();
        var createCount = 0;
        var dropCount = 0;
        indexManager
            .Setup(m => m.CreateManyAsync(
                It.IsAny<IEnumerable<CreateIndexModel<TimeoutData>>>(),
                It.IsAny<CancellationToken>()))
            .Returns(() => { Interlocked.Increment(ref createCount); return Task.FromResult(new List<string>().AsEnumerable()); });
        indexManager
            .Setup(m => m.DropOneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => { Interlocked.Increment(ref dropCount); return Task.CompletedTask; });

        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.SetupGet(c => c.Indexes).Returns(indexManager.Object);
        collection
            .Setup(c => c.InsertOneAsync(It.IsAny<TimeoutData>(), It.IsAny<InsertOneOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<TimeoutData>(It.IsAny<string>(), It.IsAny<MongoCollectionSettings?>()))
            .Returns(collection.Object);

        var settings = MongoClientSettings.FromConnectionString("mongodb://localhost:27017");
        settings.WriteConcern = WriteConcern.Acknowledged;
        var client = new Mock<IMongoClient>();
        client.SetupGet(c => c.Settings).Returns(settings);
        client.Setup(c => c.GetDatabase(It.IsAny<string>(), It.IsAny<MongoDatabaseSettings?>())).Returns(database.Object);

        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = "mongodb://localhost:27017",
            DatabaseName = "tests",
            TimeoutBatchSize = 100,
            TimeoutLockLeaseDuration = TimeSpan.FromMinutes(1),
        };
        var store = new MongoDbTimeoutStore(client.Object, options, NullLogger<MongoDbTimeoutStore>.Instance);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => store.InsertTimeoutAsync(new TimeoutData { Id = Guid.NewGuid(), Time = DateTimeOffset.UtcNow }))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, createCount);
        Assert.Equal(1, dropCount);
    }
}
