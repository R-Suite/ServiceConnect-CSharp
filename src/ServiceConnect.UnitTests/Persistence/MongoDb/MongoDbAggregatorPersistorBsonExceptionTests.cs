using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.MongoDb;
using ServiceConnect.UnitTests.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence.MongoDb;

[Collection("Mongo Bson serial")]
public class MongoDbAggregatorPersistorBsonExceptionTests
{
    /// <summary>
    /// Target type used to prove deserialization: a BsonDocument whose Body field is an
    /// array (incompatible with string) triggers BsonSerializationException.
    /// </summary>
    public sealed class CorruptTarget : IHasCorrelationId
    {
        public Guid CorrelationId { get; init; }
        public string Body { get; set; } = string.Empty;
    }

    private delegate bool TryResolveCallback(string typeName, out Type? type);

    [Fact]
    public async Task GetSnapshotAsync_DocFailsToDeserialise_DocCountedAsUnresolved_NotThrown()
    {
        var (persistor, mockCollection, registry) = CreateMockedPersistor();

        var validDoc = new MongoDbAggregatorPersistor.AggregatorDocument
        {
            Id = Guid.NewGuid(),
            Name = "agg-corrupt",
            DataTypeName = typeof(CorruptTarget).FullName!,
            // Body is a string — deserializes cleanly.
            DataBson = new BsonDocument { { "Body", "ok" } },
            Version = 1,
            InsertedAtTicks = 1L,
        };

        var corruptDoc = new MongoDbAggregatorPersistor.AggregatorDocument
        {
            Id = Guid.NewGuid(),
            Name = "agg-corrupt",
            DataTypeName = typeof(CorruptTarget).FullName!,
            // Body declared as string; supplying an array causes BsonSerializationException.
            DataBson = new BsonDocument { { "Body", new BsonArray { 1, 2, 3 } } },
            Version = 1,
            InsertedAtTicks = 2L,
        };

        registry.Setup(r => r.TryResolve(typeof(CorruptTarget).FullName!, out It.Ref<Type?>.IsAny!))
            .Returns(new TryResolveCallback((string _, out Type? t) =>
            {
                t = typeof(CorruptTarget);
                return true;
            }));

        var docs = new List<MongoDbAggregatorPersistor.AggregatorDocument> { validDoc, corruptDoc };

        var fakeCursor = new FakeAsyncCursor<MongoDbAggregatorPersistor.AggregatorDocument>(docs);

        var mockFindFluent = new Mock<IFindFluent<MongoDbAggregatorPersistor.AggregatorDocument, MongoDbAggregatorPersistor.AggregatorDocument>>();
        mockFindFluent
            .Setup(f => f.Sort(It.IsAny<SortDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>()))
            .Returns(mockFindFluent.Object);
        mockFindFluent
            .Setup(f => f.ToCursorAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeCursor);

        // Find(filter, options) is the underlying virtual method called by the Find(filter) extension.
        mockCollection
            .Setup(c => c.FindSync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<FindOptions<MongoDbAggregatorPersistor.AggregatorDocument, MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CancellationToken>()))
            .Returns(fakeCursor);

        // IMongoCollection.FindAsync is the async path used by the Find extension method.
        mockCollection
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<FindOptions<MongoDbAggregatorPersistor.AggregatorDocument, MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeCursor);

        var snapshot = await persistor.GetSnapshotAsync("agg-corrupt");

        Assert.Equal(1, snapshot.UnresolvedCount);
        Assert.Single(snapshot.ResolvedMessages);
        var resolved = Assert.IsType<CorruptTarget>(snapshot.ResolvedMessages[0]);
        Assert.Equal("ok", resolved.Body);
    }

    // ── BsonException must be wrapped in PersistenceException ─────────────────

    [Fact]
    public async Task InsertDataAsync_BsonSerializationException_WrappedInPersistenceException()
    {
        var (persistor, mockCollection, _) = CreateMockedPersistor();

        // InsertDataAsync now upserts via UpdateOneAsync to enforce idempotency on the
        // (Name, IdempotencyKey) compound; the mocked Bson failure surfaces from there.
        mockCollection
            .Setup(c => c.UpdateOneAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<UpdateDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BsonSerializationException("bson boom"));

        var ex = await Assert.ThrowsAsync<PersistenceException>(() =>
            persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "test-name", Guid.NewGuid().ToString()));

        Assert.IsAssignableFrom<BsonException>(ex.InnerException);
    }

    [Fact]
    public async Task GetSnapshotAsync_BsonSerializationException_WrappedInPersistenceException()
    {
        var (persistor, mockCollection, _) = CreateMockedPersistor();

        // FindAsync is called by the Find(...) extension inside GetSnapshotAsync.
        mockCollection
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<FindOptions<MongoDbAggregatorPersistor.AggregatorDocument, MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BsonSerializationException("bson boom"));

        var ex = await Assert.ThrowsAsync<PersistenceException>(() =>
            persistor.GetSnapshotAsync("test-name"));

        Assert.IsAssignableFrom<BsonException>(ex.InnerException);
    }

    [Fact]
    public async Task RemoveDataAsync_BsonSerializationException_WrappedInPersistenceException()
    {
        var (persistor, mockCollection, _) = CreateMockedPersistor();

        // RemoveDataAsync now uses FindOneAndDeleteAsync (returns the deleted doc so the
        // lease state can be inspected). Mock that path instead.
        mockCollection
            .Setup(c => c.FindOneAndDeleteAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<FindOneAndDeleteOptions<MongoDbAggregatorPersistor.AggregatorDocument, MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BsonSerializationException("bson boom"));

        var ex = await Assert.ThrowsAsync<PersistenceException>(() =>
            persistor.RemoveDataAsync("test-name", Guid.NewGuid()));

        Assert.IsAssignableFrom<BsonException>(ex.InnerException);
    }

    [Fact]
    public async Task RemoveAllAsync_BsonSerializationException_WrappedInPersistenceException()
    {
        var (persistor, mockCollection, _) = CreateMockedPersistor();

        mockCollection
            .Setup(c => c.DeleteManyAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BsonSerializationException("bson boom"));

        var ex = await Assert.ThrowsAsync<PersistenceException>(() =>
            persistor.RemoveAllAsync("test-name"));

        Assert.IsAssignableFrom<BsonException>(ex.InnerException);
    }

    [Fact]
    public async Task RemoveSnapshotAsync_BsonSerializationException_WrappedInPersistenceException()
    {
        var (persistor, mockCollection, _) = CreateMockedPersistor();

        mockCollection
            .Setup(c => c.DeleteManyAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BsonSerializationException("bson boom"));

        // A snapshot with at least one id so RemoveSnapshotAsync doesn't early-return.
        var snapshot = new TestSnapshot([Guid.NewGuid()]);

        var ex = await Assert.ThrowsAsync<PersistenceException>(() =>
            persistor.RemoveSnapshotAsync("test-name", snapshot));

        Assert.IsAssignableFrom<BsonException>(ex.InnerException);
    }

    [Fact]
    public async Task CountAsync_BsonSerializationException_WrappedInPersistenceException()
    {
        var (persistor, mockCollection, _) = CreateMockedPersistor();

        mockCollection
            .Setup(c => c.CountDocumentsAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CountOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BsonSerializationException("bson boom"));

        var ex = await Assert.ThrowsAsync<PersistenceException>(() =>
            persistor.CountAsync("test-name"));

        Assert.IsAssignableFrom<BsonException>(ex.InnerException);
    }

    // Minimal IAggregatorSnapshot implementation used by RemoveSnapshotAsync tests.
    private sealed class TestSnapshot(IReadOnlyList<Guid> ids) : IAggregatorSnapshot
    {
        public IReadOnlyList<IHasCorrelationId> ResolvedMessages => [];
        public IReadOnlyList<Guid> ResolvedIds => ids;
        public int UnresolvedCount => 0;
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

        return (persistor, mockCollection, registry);
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
