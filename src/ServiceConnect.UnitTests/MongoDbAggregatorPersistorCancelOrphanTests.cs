using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Verifies that GetSnapshotAsync attempts a best-effort lease release when
/// UpdateManyAsync or the subsequent read-back throws, regardless of where in
/// the call the exception fires.  The key invariant: leaseClaimed is set to
/// true BEFORE the UpdateMany await so the catch path always gates correctly.
/// </summary>
[Collection("Mongo Bson serial")]
public class MongoDbAggregatorPersistorCancelOrphanTests
{
    static MongoDbAggregatorPersistorCancelOrphanTests()
    {
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    private static (MongoDbAggregatorPersistor Persistor,
                    Mock<IMongoCollection<MongoDbAggregatorPersistor.AggregatorDocument>> Collection,
                    Mock<ILogger<MongoDbAggregatorPersistor>> Logger)
        BuildPersistor()
    {
        var mockIndexManager = new Mock<IMongoIndexManager<MongoDbAggregatorPersistor.AggregatorDocument>>();
        mockIndexManager
            .Setup(m => m.CreateManyAsync(
                It.IsAny<IEnumerable<CreateIndexModel<MongoDbAggregatorPersistor.AggregatorDocument>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var mockCollection = new Mock<IMongoCollection<MongoDbAggregatorPersistor.AggregatorDocument>>();
        mockCollection.SetupGet(c => c.Indexes).Returns(mockIndexManager.Object);

        var mockDatabase = new Mock<IMongoDatabase>();
        mockDatabase
            .Setup(db => db.GetCollection<MongoDbAggregatorPersistor.AggregatorDocument>(
                It.IsAny<string>(),
                It.IsAny<MongoCollectionSettings>()))
            .Returns(mockCollection.Object);

        var mockClient = new Mock<IMongoClient>();
        mockClient
            .SetupGet(c => c.Settings)
            .Returns(new MongoClientSettings { WriteConcern = WriteConcern.W1 });
        mockClient
            .Setup(c => c.GetDatabase(It.IsAny<string>(), It.IsAny<MongoDatabaseSettings>()))
            .Returns(mockDatabase.Object);
        // Force unsessioned path — standalone Mongo doesn't support sessions.
        mockClient
            .Setup(c => c.StartSessionAsync(It.IsAny<ClientSessionOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("standalone"));

        var logger = new Mock<ILogger<MongoDbAggregatorPersistor>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var persistor = new MongoDbAggregatorPersistor(
            mockClient.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test-db" },
            logger.Object,
            Mock.Of<IMessageTypeRegistry>());

        return (persistor, mockCollection, logger);
    }

    [Fact]
    public async Task GetSnapshotAsync_CancelAfterUpdateMany_ReleasesLeaseBestEffort()
    {
        var (persistor, collection, _) = BuildPersistor();

        var updateManyCalls = 0;
        collection
            .Setup(c => c.UpdateManyAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<UpdateDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>,
                      UpdateDefinition<MongoDbAggregatorPersistor.AggregatorDocument>,
                      UpdateOptions,
                      CancellationToken>(
                (_, _, _, ct) =>
                {
                    updateManyCalls++;
                    if (updateManyCalls == 2)
                    {
                        // Release call must use CancellationToken.None so the cancelled
                        // caller token cannot preempt cleanup.
                        Assert.Equal(CancellationToken.None, ct);
                    }
                })
            .ReturnsAsync(new UpdateResult.Acknowledged(0, 0, null));

        // The read-back FindAsync throws OCE, simulating cancellation observed after the
        // claim committed server-side.
        collection
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<FindOptions<MongoDbAggregatorPersistor.AggregatorDocument, MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("simulated"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => persistor.GetSnapshotAsync("test-agg"));

        Assert.Equal(2, updateManyCalls); // claim + best-effort release
    }

    [Fact]
    public async Task GetSnapshotAsync_CancelDuringUpdateMany_AttemptsBestEffortRelease()
    {
        // Cancellation observed by the claim's await may have arrived either before or after
        // the server actually committed the lock-update — the caller cannot tell. Setting
        // leaseClaimed before the await ensures a release attempt always fires on throw;
        // the release filter is gated on LockedBy == sessionId so a release call for a
        // claim that never committed is a server-side no-op.
        var (persistor, collection, _) = BuildPersistor();

        var updateManyCalls = 0;
        collection
            .Setup(c => c.UpdateManyAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<UpdateDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>,
                     UpdateDefinition<MongoDbAggregatorPersistor.AggregatorDocument>,
                     UpdateOptions,
                     CancellationToken>(
                (_, _, _, ct) =>
                {
                    updateManyCalls++;
                    if (updateManyCalls == 1)
                    {
                        throw new OperationCanceledException("simulated");
                    }
                    // Release call must use CancellationToken.None.
                    Assert.Equal(CancellationToken.None, ct);
                    return Task.FromResult<UpdateResult>(new UpdateResult.Acknowledged(0, 0, null));
                });

        await Assert.ThrowsAsync<OperationCanceledException>(() => persistor.GetSnapshotAsync("test-agg"));

        Assert.Equal(2, updateManyCalls); // failed claim + best-effort release
    }

    [Fact]
    public async Task GetSnapshotAsync_CancelDuringBestEffortRelease_SwallowsAndPropagatesOriginalOce()
    {
        var (persistor, collection, logger) = BuildPersistor();

        var updateManyCalls = 0;
        collection
            .Setup(c => c.UpdateManyAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<UpdateDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>,
                     UpdateDefinition<MongoDbAggregatorPersistor.AggregatorDocument>,
                     UpdateOptions,
                     CancellationToken>(
                (_, _, _, _) =>
                {
                    updateManyCalls++;
                    if (updateManyCalls == 1)
                    {
                        return Task.FromResult<UpdateResult>(new UpdateResult.Acknowledged(0, 0, null));
                    }
                    // Release attempt also fails — should be swallowed and logged.
                    throw new MongoException("simulated release failure");
                });

        // Read-back FindAsync throws OCE, triggering the catch path.
        collection
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<FindOptions<MongoDbAggregatorPersistor.AggregatorDocument, MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("simulated"));

        await Assert.ThrowsAsync<OperationCanceledException>(() => persistor.GetSnapshotAsync("test-agg"));

        Assert.Equal(2, updateManyCalls); // claim + failed release
        // Release failure must be logged as a Warning and not re-thrown.
        logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<MongoException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
