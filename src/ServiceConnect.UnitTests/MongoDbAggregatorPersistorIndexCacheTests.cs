using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Verifies the per-instance index cache: after a successful (or benign-conflict)
/// CreateManyAsync call the flag is set and subsequent operations skip the round-trip
/// entirely. Non-benign errors leave the flag unset so the next caller retries index
/// creation.
/// </summary>
[Collection("Mongo Bson serial")]
public class MongoDbAggregatorPersistorIndexCacheTests
{
    [Fact]
    public async Task EnsureIndexes_CalledTwice_OnlyHitsCreateManyAsyncOnce()
    {
        var (persistor, _, indexes) = BuildPersistorWithIndexCapture();
        var data = new AggregatorTestData(Guid.NewGuid());

        await persistor.InsertDataAsync(data, "test-name");
        await persistor.InsertDataAsync(data, "test-name");

        indexes.Verify(m => m.CreateManyAsync(
            It.IsAny<IEnumerable<CreateIndexModel<MongoDbAggregatorPersistor.AggregatorDocument>>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureIndexes_BenignConflict85_FlipsCacheFlag()
    {
        var (persistor, _, indexes) = BuildPersistorWithIndexCapture();

        // Wire CreateManyAsync to throw code 85 on first call; the flag should still flip.
        var connectionId = new MongoDB.Driver.Core.Connections.ConnectionId(
            new MongoDB.Driver.Core.Servers.ServerId(
                new MongoDB.Driver.Core.Clusters.ClusterId(),
                new System.Net.DnsEndPoint("localhost", 27017)));
        var result = new BsonDocument { ["ok"] = 0, ["code"] = 85, ["errmsg"] = "options conflict" };
        var command = new BsonDocument { ["createIndexes"] = "Aggregator" };
        indexes.SetupSequence(m => m.CreateManyAsync(
                It.IsAny<IEnumerable<CreateIndexModel<MongoDbAggregatorPersistor.AggregatorDocument>>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MongoCommandException(connectionId, "options conflict", command, result))
            .ReturnsAsync(["ok"]);  // second call should be skipped via the cache

        var data = new AggregatorTestData(Guid.NewGuid());
        await persistor.InsertDataAsync(data, "test-name");   // benign 85 → flag flips
        await persistor.InsertDataAsync(data, "test-name");   // skipped via cache

        indexes.Verify(m => m.CreateManyAsync(
            It.IsAny<IEnumerable<CreateIndexModel<MongoDbAggregatorPersistor.AggregatorDocument>>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureIndexes_NonBenignError_LeavesFlagUnflipped()
    {
        var (persistor, _, indexes) = BuildPersistorWithIndexCapture();

        var connectionId = new MongoDB.Driver.Core.Connections.ConnectionId(
            new MongoDB.Driver.Core.Servers.ServerId(
                new MongoDB.Driver.Core.Clusters.ClusterId(),
                new System.Net.DnsEndPoint("localhost", 27017)));
        var result = new BsonDocument { ["ok"] = 0, ["code"] = 13, ["errmsg"] = "unauthorized" };
        var command = new BsonDocument { ["createIndexes"] = "Aggregator" };

        indexes.Setup(m => m.CreateManyAsync(
                It.IsAny<IEnumerable<CreateIndexModel<MongoDbAggregatorPersistor.AggregatorDocument>>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MongoCommandException(connectionId, "unauthorized", command, result));

        var data = new AggregatorTestData(Guid.NewGuid());
        // Non-benign MongoCommandException is wrapped in PersistenceException.
        await Assert.ThrowsAsync<PersistenceException>(() => persistor.InsertDataAsync(data, "test-name"));
        await Assert.ThrowsAsync<PersistenceException>(() => persistor.InsertDataAsync(data, "test-name"));

        // Flag should NOT have flipped — both calls retry index creation.
        indexes.Verify(m => m.CreateManyAsync(
            It.IsAny<IEnumerable<CreateIndexModel<MongoDbAggregatorPersistor.AggregatorDocument>>>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private static (MongoDbAggregatorPersistor persistor,
                    Mock<IMongoCollection<MongoDbAggregatorPersistor.AggregatorDocument>> collection,
                    Mock<IMongoIndexManager<MongoDbAggregatorPersistor.AggregatorDocument>> indexes)
        BuildPersistorWithIndexCapture()
    {
        var mockDatabase = new Mock<IMongoDatabase>();
        var mockClient = new Mock<IMongoClient>();
        var mockCollection = new Mock<IMongoCollection<MongoDbAggregatorPersistor.AggregatorDocument>>();
        var mockIndexManager = new Mock<IMongoIndexManager<MongoDbAggregatorPersistor.AggregatorDocument>>();

        mockClient
            .SetupGet(c => c.Settings)
            .Returns(new MongoClientSettings { WriteConcern = WriteConcern.W1 });
        mockClient
            .Setup(c => c.GetDatabase(It.IsAny<string>(), It.IsAny<MongoDatabaseSettings>()))
            .Returns(mockDatabase.Object);

        mockDatabase
            .Setup(db => db.GetCollection<MongoDbAggregatorPersistor.AggregatorDocument>(
                It.IsAny<string>(),
                It.IsAny<MongoCollectionSettings>()))
            .Returns(mockCollection.Object);

        mockCollection
            .SetupGet(c => c.Indexes)
            .Returns(mockIndexManager.Object);

        // Default: CreateManyAsync succeeds.
        mockIndexManager
            .Setup(m => m.CreateManyAsync(
                It.IsAny<IEnumerable<CreateIndexModel<MongoDbAggregatorPersistor.AggregatorDocument>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // InsertOneAsync must succeed so tests that don't care about the insert don't fail there.
        mockCollection
            .Setup(c => c.InsertOneAsync(
                It.IsAny<MongoDbAggregatorPersistor.AggregatorDocument>(),
                It.IsAny<InsertOneOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var persistor = new MongoDbAggregatorPersistor(
            mockClient.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test-db" },
            Mock.Of<ILogger<MongoDbAggregatorPersistor>>(),
            Mock.Of<IMessageTypeRegistry>());

        return (persistor, mockCollection, mockIndexManager);
    }
}
