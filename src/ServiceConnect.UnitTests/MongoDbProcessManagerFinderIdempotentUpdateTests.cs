using System.Reflection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

[Collection("Mongo Bson serial")]
public class MongoDbProcessManagerFinderIdempotentUpdateTests
{
    [Fact]
    public async Task UpdateDataAsync_MatchedButNotModified_DoesNotThrowConcurrencyException()
    {
        var finder = CreateFinder(out var database, out _);
        var collection = new Mock<IMongoCollection<MongoDbData<TestProcessManagerData>>>();
        var versionedData = new MongoDbData<TestProcessManagerData>
        {
            Id = Guid.NewGuid(),
            Version = 4,
            Data = new TestProcessManagerData()
        };

        var indexedCollections = (System.Collections.Concurrent.ConcurrentDictionary<string, bool>)typeof(MongoDbProcessManagerFinder)
            .GetField("_indexedCollections", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(finder)!;
        indexedCollections.TryAdd(MongoDbProcessManagerFinder.SanitizeCollectionName(typeof(TestProcessManagerData).FullName!), true);

        database.Setup(db => db.GetCollection<MongoDbData<TestProcessManagerData>>(
                MongoDbProcessManagerFinder.SanitizeCollectionName(typeof(TestProcessManagerData).FullName!),
                It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);

        // Idempotent server response: row matched the filter but no field bytes changed
        // (e.g., replacement document identical). MatchedCount=1, ModifiedCount=0.
        collection.Setup(c => c.ReplaceOneAsync(
                It.IsAny<FilterDefinition<MongoDbData<TestProcessManagerData>>>(),
                It.IsAny<MongoDbData<TestProcessManagerData>>(),
                It.IsAny<ReplaceOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReplaceOneResult.Acknowledged(matchedCount: 1, modifiedCount: 0, upsertedId: null));

        // Should NOT throw — the row was found at the expected version, that's all
        // optimistic concurrency cares about.
        await finder.UpdateDataAsync(versionedData, CancellationToken.None);

        // Caller's version must reflect the bump because the write was acknowledged
        // and the row was found at the expected version.
        Assert.Equal(5L, versionedData.Version);
    }

    [Fact]
    public async Task UpdateDataAsync_NoMatch_ThrowsConcurrencyException()
    {
        // Sanity check: when the filter doesn't match (stale version / row gone),
        // MatchedCount=0 and ModifiedCount=0 — concurrency error must still fire.
        var finder = CreateFinder(out var database, out _);
        var collection = new Mock<IMongoCollection<MongoDbData<TestProcessManagerData>>>();
        var versionedData = new MongoDbData<TestProcessManagerData>
        {
            Id = Guid.NewGuid(),
            Version = 4,
            Data = new TestProcessManagerData()
        };

        var indexedCollections = (System.Collections.Concurrent.ConcurrentDictionary<string, bool>)typeof(MongoDbProcessManagerFinder)
            .GetField("_indexedCollections", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(finder)!;
        indexedCollections.TryAdd(MongoDbProcessManagerFinder.SanitizeCollectionName(typeof(TestProcessManagerData).FullName!), true);

        database.Setup(db => db.GetCollection<MongoDbData<TestProcessManagerData>>(
                MongoDbProcessManagerFinder.SanitizeCollectionName(typeof(TestProcessManagerData).FullName!),
                It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);

        collection.Setup(c => c.ReplaceOneAsync(
                It.IsAny<FilterDefinition<MongoDbData<TestProcessManagerData>>>(),
                It.IsAny<MongoDbData<TestProcessManagerData>>(),
                It.IsAny<ReplaceOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReplaceOneResult.Acknowledged(matchedCount: 0, modifiedCount: 0, upsertedId: null));

        await Assert.ThrowsAsync<ServiceConnect.Interfaces.Exceptions.ConcurrencyException>(
            () => finder.UpdateDataAsync(versionedData, CancellationToken.None));

        // Version must NOT have been bumped on a failed update.
        Assert.Equal(4L, versionedData.Version);
    }

    private static MongoDbProcessManagerFinder CreateFinder(
        out Mock<IMongoDatabase> database,
        out Mock<IMongoClient> client)
    {
        database = new Mock<IMongoDatabase>();
        client = new Mock<IMongoClient>();
        client.Setup(c => c.GetDatabase("test-db", It.IsAny<MongoDatabaseSettings>()))
            .Returns(database.Object);
        client.SetupGet(c => c.Settings).Returns(new MongoClientSettings());

        return new MongoDbProcessManagerFinder(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test-db" },
            Mock.Of<ILogger<MongoDbProcessManagerFinder>>());
    }

    public sealed class TestProcessManagerData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; } = Guid.NewGuid();
    }
}
