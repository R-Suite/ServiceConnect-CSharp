using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence.MongoDb;

[Collection("Mongo Bson serial")]
public class MongoDbAggregatorPersistorNullDataTests
{
    [Fact]
    public async Task InsertDataAsync_NullData_ThrowsArgumentNullException()
    {
        var persistor = CreatePersistor();

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            persistor.InsertDataAsync(data: null!, name: "agg", idempotencyKey: Guid.NewGuid().ToString()));

        Assert.Equal("data", ex.ParamName);
    }

    private static MongoDbAggregatorPersistor CreatePersistor()
    {
        var client = new Mock<IMongoClient>();
        var database = new Mock<IMongoDatabase>();
        var collection = new Mock<IMongoCollection<MongoDbAggregatorPersistor.AggregatorDocument>>();

        client.SetupGet(c => c.Settings).Returns(new MongoClientSettings { WriteConcern = WriteConcern.W1 });
        client.Setup(c => c.GetDatabase("test-db", It.IsAny<MongoDatabaseSettings>()))
            .Returns(database.Object);
        database.Setup(d => d.GetCollection<MongoDbAggregatorPersistor.AggregatorDocument>(
                "Aggregator", It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);

        return new MongoDbAggregatorPersistor(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test-db" },
            Mock.Of<ILogger<MongoDbAggregatorPersistor>>(),
            Mock.Of<IMessageTypeRegistry>());
    }
}
