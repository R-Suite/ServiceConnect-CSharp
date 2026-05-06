using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoDbTimeoutStoreGuidEmptyTests
{
    private static MongoDbTimeoutStore CreateStore()
    {
        var indexes = new Mock<IMongoIndexManager<TimeoutData>>();
        indexes.Setup(m => m.CreateManyAsync(
                It.IsAny<IEnumerable<CreateIndexModel<TimeoutData>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(["ok"]);
        indexes.Setup(m => m.DropOneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.SetupGet(c => c.Indexes).Returns(indexes.Object);

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<TimeoutData>("Timeouts", null)).Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.SetupGet(c => c.Settings).Returns(new MongoClientSettings { WriteConcern = WriteConcern.W1 });
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);

        return new MongoDbTimeoutStore(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test" },
            NullLogger<MongoDbTimeoutStore>.Instance);
    }

    [Fact]
    public async Task InsertTimeoutAsync_NullTimeoutData_ThrowsArgumentNullException()
    {
        var store = CreateStore();

        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            store.InsertTimeoutAsync(null!, CancellationToken.None));

        Assert.Equal("timeoutData", ex.ParamName);
    }

    [Fact]
    public async Task InsertTimeoutAsync_GuidEmptyId_ThrowsArgumentException()
    {
        var store = CreateStore();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.InsertTimeoutAsync(
                new TimeoutData { Id = Guid.Empty, Time = DateTime.UtcNow.AddMinutes(1) },
                CancellationToken.None));

        Assert.Equal("timeoutData", ex.ParamName);
    }
}
