using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Verifies that ReleaseSnapshotAsync always attempts the session-id-gated UpdateMany,
/// even when every row in the snapshot failed type resolution (ResolvedIds is empty).
/// The release filter matches on LockedBy == sessionId, so it is a server-side no-op
/// when nothing was actually claimed — but it correctly releases rows that were.
/// </summary>
[Collection("Mongo Bson serial")]
public class MongoDbAggregatorPersistorReleaseSnapshotTests
{
    static MongoDbAggregatorPersistorReleaseSnapshotTests()
    {
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    private static (MongoDbAggregatorPersistor Persistor,
                    Mock<IMongoCollection<MongoDbAggregatorPersistor.AggregatorDocument>> Collection,
                    Mock<IMessageTypeRegistry> Registry)
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
        // Simulate a standalone server so GetSnapshotAsync takes the unsessioned path.
        mockClient
            .Setup(c => c.StartSessionAsync(It.IsAny<ClientSessionOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("standalone"));

        var registry = new Mock<IMessageTypeRegistry>();

        var persistor = new MongoDbAggregatorPersistor(
            mockClient.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test-db" },
            Mock.Of<ILogger<MongoDbAggregatorPersistor>>(),
            registry.Object);

        return (persistor, mockCollection, registry);
    }

    [Fact]
    public async Task ReleaseSnapshotAsync_AllUnresolved_StillInvokesSessionIdGatedRelease()
    {
        // Arrange: one document whose type cannot be resolved → UnresolvedCount = 1, ResolvedIds empty.
        var (persistor, collection, registry) = BuildPersistor();

        registry
            .Setup(r => r.TryResolve(It.IsAny<string>(), out It.Ref<Type?>.IsAny!))
            .Returns(false);

        var doc = new MongoDbAggregatorPersistor.AggregatorDocument
        {
            Id = Guid.NewGuid(),
            Name = "agg-unresolved",
            DataTypeName = "Unknown.Type",
            DataBson = [],
            Version = 1,
            InsertedAtTicks = 1L,
        };

        var fakeCursor = new FakeAsyncCursor<MongoDbAggregatorPersistor.AggregatorDocument>([doc]);

        collection
            .Setup(c => c.UpdateManyAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<UpdateDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateResult.Acknowledged(1, 1, null));

        // FindAsync is routed through by the Find extension used in GetSnapshotAsync.
        collection
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<FindOptions<MongoDbAggregatorPersistor.AggregatorDocument, MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeCursor);

        // Act: get snapshot (all-unresolved) then release it.
        var snapshot = await persistor.GetSnapshotAsync("agg-unresolved");

        Assert.Empty(snapshot.ResolvedIds);
        Assert.Equal(1, snapshot.UnresolvedCount);

        // Reset call count so we can assert only the release call.
        collection.Invocations.Clear();

        await persistor.ReleaseSnapshotAsync("agg-unresolved", snapshot);

        // Assert: UpdateManyAsync must have been invoked (session-id-gated release).
        collection.Verify(c => c.UpdateManyAsync(
            It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
            It.IsAny<UpdateDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
            It.IsAny<UpdateOptions>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Single-batch in-memory cursor that returns all items in one MoveNext call.
    /// </summary>
    private sealed class FakeAsyncCursor<T>(List<T> items) : IAsyncCursor<T>
    {
        private readonly List<T> _items = items;
        private bool _moved;

        public IEnumerable<T> Current => _items;

        public bool MoveNext(CancellationToken cancellationToken = default)
        {
            if (_moved)
            {
                return false;
            }

            _moved = true;
            return true;
        }

        public Task<bool> MoveNextAsync(CancellationToken cancellationToken = default)
        {
            if (_moved)
            {
                return Task.FromResult(false);
            }

            _moved = true;
            return Task.FromResult(true);
        }

        public void Dispose() { }
    }
}
