using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

[Collection("Mongo Bson serial")]
public class MongoDbTimeoutStoreSortTieBreakerTests
{
    static MongoDbTimeoutStoreSortTieBreakerTests()
    {
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    [Fact]
    public async Task GetTimeoutsBatch_CandidateSort_IncludesIdTieBreaker()
    {
        FindOptions<TimeoutData, Guid>? capturedOptions = null;

        var indexes = new Mock<IMongoIndexManager<TimeoutData>>();
        indexes.Setup(m => m.CreateManyAsync(It.IsAny<IEnumerable<CreateIndexModel<TimeoutData>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["ok"]);
        indexes.Setup(m => m.DropOneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.SetupGet(c => c.Indexes).Returns(indexes.Object);

        var emptyCursor = new Mock<IAsyncCursor<Guid>>();
        emptyCursor.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        emptyCursor.SetupGet(c => c.Current).Returns([]);
        collection.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<FindOptions<TimeoutData, Guid>>(),
                It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<TimeoutData>, FindOptions<TimeoutData, Guid>, CancellationToken>(
                (_, opts, _) => capturedOptions = opts)
            .ReturnsAsync(emptyCursor.Object);

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<TimeoutData>("Timeouts", null)).Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.SetupGet(c => c.Settings).Returns(new MongoClientSettings { WriteConcern = WriteConcern.W1 });
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);
        // No session — keeps the test simple; the sort shape is the same with or without.
        client.Setup(c => c.StartSessionAsync(It.IsAny<ClientSessionOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("test"));

        var store = new MongoDbTimeoutStore(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test" },
            NullLogger<MongoDbTimeoutStore>.Instance);

        await store.GetTimeoutsBatchAsync();

        Assert.NotNull(capturedOptions);
        Assert.NotNull(capturedOptions!.Sort);
        var sortJson = capturedOptions.Sort.Render(new RenderArgs<TimeoutData>(
            BsonSerializer.LookupSerializer<TimeoutData>(),
            BsonSerializer.SerializerRegistry)).ToJson();
        Assert.Contains("\"Time\" : 1", sortJson);
        Assert.Contains("\"_id\" : 1", sortJson);
    }
}
