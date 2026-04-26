using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoDbAggregatorPersistorBsonExceptionTests
{
    /// <summary>
    /// Target type used to prove deserialization: a BsonDocument whose Body field is an
    /// array (incompatible with string) triggers BsonSerializationException.
    /// </summary>
    public sealed class CorruptTarget
    {
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
