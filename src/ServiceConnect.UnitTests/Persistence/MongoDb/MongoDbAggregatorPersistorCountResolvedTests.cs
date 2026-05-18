using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence.MongoDb;

/// <summary>
/// Verifies the Mongo aggregator persistor implements CountResolvedAsync as a typed
/// $in query against the registered type-name set, so the AggregatorProcessor's
/// batch-size flush gate is not triggered by unresolved-only batches.
/// </summary>
[Collection("Mongo Bson serial")]
public class MongoDbAggregatorPersistorCountResolvedTests
{
    static MongoDbAggregatorPersistorCountResolvedTests()
    {
        // Match the established Mongo-test pattern: drive Guid-serializer setup from the
        // class cctor so a parallel test runner cannot land on an uninitialised state when
        // this test class loads first.
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    [Fact]
    public async Task CountResolvedAsync_NoRegisteredTypes_ReturnsZeroWithoutHittingDb()
    {
        var (persistor, mockCollection, registry) = CreateMockedPersistor();
        registry.Setup(r => r.AllRegisteredTypeNames()).Returns([]);

        var count = await persistor.CountResolvedAsync("agg");

        Assert.Equal(0, count);
        // Round-trip is skipped — no CountDocumentsAsync should have been issued.
        mockCollection.Verify(
            c => c.CountDocumentsAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CountOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CountResolvedAsync_RegisteredTypes_IssuesCountDocumentsWithRegisteredSet()
    {
        var (persistor, mockCollection, registry) = CreateMockedPersistor();
        registry.Setup(r => r.AllRegisteredTypeNames())
            .Returns(["Foo.Type1", "Foo.Type2"]);
        mockCollection
            .Setup(c => c.CountDocumentsAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CountOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(7L);

        var count = await persistor.CountResolvedAsync("agg-r");

        Assert.Equal(7, count);
        registry.Verify(r => r.AllRegisteredTypeNames(), Times.Once);
        mockCollection.Verify(
            c => c.CountDocumentsAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CountOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CountResolvedAsync_ClampsAtIntMaxValue()
    {
        var (persistor, mockCollection, registry) = CreateMockedPersistor();
        registry.Setup(r => r.AllRegisteredTypeNames()).Returns(["Foo.T"]);
        mockCollection
            .Setup(c => c.CountDocumentsAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CountOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((long)int.MaxValue + 1L);

        var count = await persistor.CountResolvedAsync("agg-max");

        Assert.Equal(int.MaxValue, count);
    }

    [Fact]
    public async Task CountResolvedAsync_BsonSerializationException_WrappedInPersistenceException()
    {
        var (persistor, mockCollection, registry) = CreateMockedPersistor();
        registry.Setup(r => r.AllRegisteredTypeNames()).Returns(["Foo.T"]);
        mockCollection
            .Setup(c => c.CountDocumentsAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CountOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BsonSerializationException("bson boom"));

        var ex = await Assert.ThrowsAsync<PersistenceException>(
            () => persistor.CountResolvedAsync("agg-bson"));

        Assert.IsAssignableFrom<BsonException>(ex.InnerException);
    }

    [Fact]
    public async Task CountResolvedAsync_MongoException_WrappedInPersistenceException()
    {
        var (persistor, mockCollection, registry) = CreateMockedPersistor();
        registry.Setup(r => r.AllRegisteredTypeNames()).Returns(["Foo.T"]);
        mockCollection
            .Setup(c => c.CountDocumentsAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CountOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MongoConnectionException(new MongoDB.Driver.Core.Connections.ConnectionId(
                new MongoDB.Driver.Core.Servers.ServerId(
                    new MongoDB.Driver.Core.Clusters.ClusterId(),
                    new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 27017))),
                "mongo down"));

        var ex = await Assert.ThrowsAsync<PersistenceException>(
            () => persistor.CountResolvedAsync("agg-mongo"));

        Assert.IsAssignableFrom<MongoException>(ex.InnerException);
    }

    private static (MongoDbAggregatorPersistor persistor,
                    Mock<IMongoCollection<MongoDbAggregatorPersistor.AggregatorDocument>> collection,
                    Mock<IMessageTypeRegistry> registry)
        CreateMockedPersistor()
    {
        var mockDatabase = new Mock<IMongoDatabase>();
        var mockClient = new Mock<IMongoClient>();
        var mockCollection = new Mock<IMongoCollection<MongoDbAggregatorPersistor.AggregatorDocument>>();
        var mockIndexManager = new Mock<IMongoIndexManager<MongoDbAggregatorPersistor.AggregatorDocument>>();
        var registry = new Mock<IMessageTypeRegistry>();

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

        mockIndexManager
            .Setup(m => m.CreateManyAsync(
                It.IsAny<IEnumerable<CreateIndexModel<MongoDbAggregatorPersistor.AggregatorDocument>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var persistor = new MongoDbAggregatorPersistor(
            mockClient.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test-db" },
            Mock.Of<ILogger<MongoDbAggregatorPersistor>>(),
            registry.Object);

        return (persistor, mockCollection, registry);
    }
}
