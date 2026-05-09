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
public class MongoDbTimeoutStoreLeaseFilterTests
{
    static MongoDbTimeoutStoreLeaseFilterTests()
    {
        // BSON Guid serializer must be registered before any filter rendering.
        // Matches the static-cctor pattern in MongoDbTimeoutStoreTests.cs.
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    private static (MongoDbTimeoutStore Store, Mock<IMongoCollection<TimeoutData>> Collection)
        BuildStoreCapturingFilters()
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

        // Default benign responses so the methods don't throw before exercising the filter.
        collection.Setup(c => c.DeleteOneAsync(It.IsAny<FilterDefinition<TimeoutData>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteResult.Acknowledged(1));
        collection.Setup(c => c.UpdateOneAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<UpdateDefinition<TimeoutData>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateResult.Acknowledged(1, 1, null));

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<TimeoutData>("Timeouts", null))
            .Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.SetupGet(c => c.Settings).Returns(new MongoClientSettings { WriteConcern = WriteConcern.W1 });
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);
        // Deterministically take the unsessioned path; standalone/older servers don't support
        // sessions, and we don't need session plumbing to test filter predicates.
        client.Setup(c => c.StartSessionAsync(
                It.IsAny<ClientSessionOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("test"));

        var store = new MongoDbTimeoutStore(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test" },
            NullLogger<MongoDbTimeoutStore>.Instance);

        return (store, collection);
    }

    private static string Render(FilterDefinition<TimeoutData> filter) =>
        filter.Render(
            BsonSerializer.LookupSerializer<TimeoutData>(),
            BsonSerializer.SerializerRegistry).ToJson();

    [Fact]
    public async Task RemoveDispatchedTimeout_FilterIncludesLockExpiresAtPredicate()
    {
        var (store, collection) = BuildStoreCapturingFilters();
        FilterDefinition<TimeoutData>? captured = null;
        collection.Setup(c => c.DeleteOneAsync(It.IsAny<FilterDefinition<TimeoutData>>(), It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<TimeoutData>, CancellationToken>((f, _) => captured = f)
            .ReturnsAsync(new DeleteResult.Acknowledged(1));

        var owner = Guid.NewGuid();
        await store.RemoveDispatchedTimeoutAsync(Guid.NewGuid(), owner);

        Assert.NotNull(captured);
        var json = Render(captured!);
        Assert.Contains("\"LockExpiresAt\"", json);
        Assert.Contains("\"$gt\"", json);
    }

    [Fact]
    public async Task ReleaseDispatchedTimeout_FilterIncludesLockExpiresAtPredicate()
    {
        var (store, collection) = BuildStoreCapturingFilters();
        FilterDefinition<TimeoutData>? captured = null;
        collection.Setup(c => c.UpdateOneAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<UpdateDefinition<TimeoutData>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<TimeoutData>, UpdateDefinition<TimeoutData>, UpdateOptions, CancellationToken>(
                (f, _, _, _) => captured = f)
            .ReturnsAsync(new UpdateResult.Acknowledged(1, 1, null));

        var owner = Guid.NewGuid();
        await store.ReleaseDispatchedTimeoutAsync(Guid.NewGuid(), owner);

        Assert.NotNull(captured);
        var json = Render(captured!);
        Assert.Contains("\"LockExpiresAt\"", json);
        Assert.Contains("\"$gt\"", json);
    }

    [Fact]
    public async Task GetTimeoutsBatch_OwnedReadBackFilter_IncludesLockExpiresAtPredicate()
    {
        // The owned read-back is the FindAsync-after-UpdateMany inside GetTimeoutsBatchAsync.
        // We need the candidate-id query to return at least one id so the read-back path fires.
        var (store, collection) = BuildStoreCapturingFilters();

        collection.Setup(c => c.UpdateManyAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<UpdateDefinition<TimeoutData>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateResult.Acknowledged(0, 0, null));

        FilterDefinition<TimeoutData>? readBackFilter = null;

        // Candidate-id FindAsync returns one id so the read-back path fires.
        var oneIdCursor = new Mock<IAsyncCursor<Guid>>();
        oneIdCursor.SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false);
        IEnumerable<Guid> oneId = [Guid.NewGuid()];
        oneIdCursor.SetupGet(c => c.Current).Returns(oneId);

        collection.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<FindOptions<TimeoutData, Guid>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(oneIdCursor.Object);

        // Read-back FindAsync<TimeoutData,TimeoutData> — capture filter, return empty.
        var emptyTimeoutCursor = new Mock<IAsyncCursor<TimeoutData>>();
        emptyTimeoutCursor.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        emptyTimeoutCursor.SetupGet(c => c.Current).Returns([]);
        collection.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<FindOptions<TimeoutData, TimeoutData>>(),
                It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<TimeoutData>, FindOptions<TimeoutData, TimeoutData>, CancellationToken>(
                (f, _, _) => readBackFilter = f)
            .ReturnsAsync(emptyTimeoutCursor.Object);

        await store.GetTimeoutsBatchAsync();

        Assert.NotNull(readBackFilter);
        var json = Render(readBackFilter!);
        Assert.Contains("\"LockExpiresAt\"", json);
        Assert.Contains("\"$gt\"", json);
    }
}
