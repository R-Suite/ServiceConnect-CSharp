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
/// M37: RemoveDataAsync must distinguish between a name that has never existed
/// (KeyNotFoundException) and a name that exists but the supplied CorrelationId
/// wasn't matched (ConcurrencyException with row count in the message).
/// </summary>
public class MongoDbAggregatorPersistorRemoveDataDistinctionTests
{
    [Fact]
    public async Task RemoveData_NoRowsForName_ThrowsKeyNotFoundException()
    {
        // DeleteOneAsync returns acknowledged with DeletedCount=0.
        // CountDocumentsAsync (Name-only filter) also returns 0 — the name bucket is empty.
        var (persistor, collection) = CreateMockedPersistor();

        collection
            .Setup(c => c.DeleteOneAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteResult.Acknowledged(0));

        collection
            .Setup(c => c.CountDocumentsAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CountOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0L);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            persistor.RemoveDataAsync("missing-name", Guid.NewGuid()));
    }

    [Fact]
    public async Task RemoveData_RowsForNameButNoCorrelation_ThrowsConcurrencyExceptionWithRowCount()
    {
        // DeleteOneAsync returns acknowledged with DeletedCount=0.
        // CountDocumentsAsync (Name-only filter) returns 5 — name bucket has rows, just not this CorrelationId.
        var (persistor, collection) = CreateMockedPersistor();

        collection
            .Setup(c => c.DeleteOneAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteResult.Acknowledged(0));

        collection
            .Setup(c => c.CountDocumentsAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CountOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(5L);

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() =>
            persistor.RemoveDataAsync("test-name", Guid.NewGuid()));

        Assert.Contains("5 row", ex.Message);
    }

    private static (MongoDbAggregatorPersistor persistor,
                    Mock<IMongoCollection<MongoDbAggregatorPersistor.AggregatorDocument>> collection)
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

        // EnsureIndexesAsync calls _collection.Indexes.CreateManyAsync(...)
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

        return (persistor, mockCollection);
    }
}
