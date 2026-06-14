using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence.MongoDb;

[Collection("Mongo Bson serial")]
public class MongoDbTimeoutStoreCancelOrphanTests
{
    static MongoDbTimeoutStoreCancelOrphanTests()
    {
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    private static (MongoDbTimeoutStore Store, Mock<IMongoCollection<TimeoutData>> Collection, Mock<ILogger<MongoDbTimeoutStore>> Logger)
        BuildStore()
    {
        var indexes = new Mock<IMongoIndexManager<TimeoutData>>();
        indexes.Setup(m => m.CreateManyAsync(It.IsAny<IEnumerable<CreateIndexModel<TimeoutData>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["ok"]);
        indexes.Setup(m => m.DropOneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.SetupGet(c => c.Indexes).Returns(indexes.Object);

        // Candidate-id FindAsync returns one id so the read-back path fires.
        var oneIdCursor = new Mock<IAsyncCursor<Guid>>();
        var seq = oneIdCursor.SetupSequence(c => c.MoveNextAsync(It.IsAny<CancellationToken>()));
        seq.ReturnsAsync(true).ReturnsAsync(false);
        oneIdCursor.SetupGet(c => c.Current).Returns([Guid.NewGuid()]);
        collection.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<FindOptions<TimeoutData, Guid>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(oneIdCursor.Object);

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<TimeoutData>("Timeouts", null)).Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.SetupGet(c => c.Settings).Returns(new MongoClientSettings { WriteConcern = WriteConcern.W1 });
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);
        // Force unsessioned path for simplicity.
        client.Setup(c => c.StartSessionAsync(It.IsAny<ClientSessionOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("standalone"));

        var logger = new Mock<ILogger<MongoDbTimeoutStore>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var store = new MongoDbTimeoutStore(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test" },
            logger.Object);

        return (store, collection, logger);
    }

    [Fact]
    public async Task GetTimeoutsBatch_CancelAfterUpdateMany_ReleasesLeaseBestEffort()
    {
        var (store, collection, _) = BuildStore();

        var updateManyCalls = 0;
        collection.Setup(c => c.UpdateManyAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<UpdateDefinition<TimeoutData>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<TimeoutData>, UpdateDefinition<TimeoutData>, UpdateOptions, CancellationToken>(
                (filter, _, _, ct) =>
                {
                    updateManyCalls++;
                    if (updateManyCalls == 2)
                    {
                        // Release call must use CancellationToken.None so it isn't cancelled.
                        Assert.Equal(CancellationToken.None, ct);
                    }
                })
            .ReturnsAsync(new UpdateResult.Acknowledged(0, 0, null));

        // Wire FindAsync<TimeoutData> (the read-back) to throw OCE.
        collection.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<FindOptions<TimeoutData, TimeoutData>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("simulated"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.GetTimeoutsBatchAsync());

        Assert.Equal(2, updateManyCalls); // claim + release
    }

    [Fact]
    public async Task GetTimeoutsBatch_CancelDuringUpdateMany_AttemptsBestEffortRelease()
    {
        // Cancellation observed by the claim's await may have arrived either before or after
        // the server actually committed the lock-update — the caller cannot tell. The store
        // marks intent to claim before the await and always runs a best-effort release on
        // throw; the release filter is gated on LockedBy == sessionId so a release call for
        // a claim that never committed is a server-side no-op.
        var (store, collection, _) = BuildStore();

        var updateManyCalls = 0;
        collection.Setup(c => c.UpdateManyAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<UpdateDefinition<TimeoutData>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<FilterDefinition<TimeoutData>, UpdateDefinition<TimeoutData>, UpdateOptions, CancellationToken>(
                (_, _, _, ct) =>
                {
                    updateManyCalls++;
                    if (updateManyCalls == 1)
                    {
                        throw new OperationCanceledException("simulated");
                    }
                    // Release call must use CancellationToken.None so cancellation can't preempt cleanup.
                    Assert.Equal(CancellationToken.None, ct);
                    return Task.FromResult<UpdateResult>(new UpdateResult.Acknowledged(0, 0, null));
                });

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.GetTimeoutsBatchAsync());

        Assert.Equal(2, updateManyCalls); // failed claim + best-effort release
    }

    [Fact]
    public async Task GetTimeoutsBatch_CancelDuringBestEffortRelease_SwallowsAndPropagatesOriginalOce()
    {
        var (store, collection, logger) = BuildStore();

        var updateManyCalls = 0;
        collection.Setup(c => c.UpdateManyAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<UpdateDefinition<TimeoutData>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<FilterDefinition<TimeoutData>, UpdateDefinition<TimeoutData>, UpdateOptions, CancellationToken>(
                (_, _, _, _) =>
                {
                    updateManyCalls++;
                    if (updateManyCalls == 1)
                    {
                        return Task.FromResult<UpdateResult>(new UpdateResult.Acknowledged(0, 0, null));
                    }
                    // Release fails too — should be swallowed and logged.
                    throw new MongoException("simulated release failure");
                });

        collection.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<FindOptions<TimeoutData, TimeoutData>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("simulated"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.GetTimeoutsBatchAsync());

        Assert.Equal(2, updateManyCalls);
        // Verify a Warning was logged (the release failure).
        logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<MongoException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
