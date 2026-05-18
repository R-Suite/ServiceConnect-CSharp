using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using ServiceConnect.UnitTests.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence.MongoDb;

/// <summary>
/// Verifies the sort shape and monotonic InsertSequence behaviour: snapshots are sorted
/// by Time then InsertSequence so equal-Time inserts stay in arrival order.
/// </summary>
[Collection("Mongo Bson serial")]
public class MongoDbAggregatorPersistorSortSequenceTests
{
    [Fact]
    public async Task GetSnapshot_SortIncludesInsertSequenceTieBreaker()
    {
        // Capture the FindOptions that GetSnapshotAsync passes to FindAsync; the sort
        // definition is embedded there by the MongoDB driver's Find extension method.
        FindOptions<MongoDbAggregatorPersistor.AggregatorDocument,
                    MongoDbAggregatorPersistor.AggregatorDocument>? capturedOptions = null;

        var (persistor, _, _) = CreateMockedPersistor(captureOptions: opts => capturedOptions = opts);

        await persistor.GetSnapshotAsync("test-name");

        Assert.NotNull(capturedOptions);
        Assert.NotNull(capturedOptions!.Sort);

        var rendered = capturedOptions.Sort!
            .Render(new RenderArgs<MongoDbAggregatorPersistor.AggregatorDocument>(
                BsonSerializer.LookupSerializer<MongoDbAggregatorPersistor.AggregatorDocument>(),
                BsonSerializer.SerializerRegistry))
            .ToJson();

        Assert.Contains("\"InsertedAtTicks\" : 1", rendered);
        Assert.Contains("\"InsertSequence\" : 1", rendered);
        // Id is the final cross-process tie-break; verify it is also present.
        Assert.Contains("\"_id\" : 1", rendered);
    }

    [Fact]
    public async Task InsertData_AssignsMonotonicInsertSequence()
    {
        // InsertDataAsync upserts via UpdateOneAsync with SetOnInsert(InsertSequence).
        // Capture each rendered UpdateDefinition and extract the InsertSequence value
        // from its $setOnInsert subdocument.
        var captured = new List<UpdateDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>();

        var (persistor, _, _) = CreateMockedPersistor(captureUpsert: captured.Add);

        await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()) { Value = "first" }, "agg", Guid.NewGuid().ToString());
        await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()) { Value = "second" }, "agg", Guid.NewGuid().ToString());

        Assert.Equal(2, captured.Count);
        var seq0 = ExtractInsertSequence(captured[0]);
        var seq1 = ExtractInsertSequence(captured[1]);
        Assert.True(seq1 > seq0,
            $"Expected InsertSequence to be monotonically increasing; got {seq0} then {seq1}");
    }

    private static long ExtractInsertSequence(UpdateDefinition<MongoDbAggregatorPersistor.AggregatorDocument> update)
    {
        var rendered = update.Render(new RenderArgs<MongoDbAggregatorPersistor.AggregatorDocument>(
            BsonSerializer.LookupSerializer<MongoDbAggregatorPersistor.AggregatorDocument>(),
            BsonSerializer.SerializerRegistry));
        var setOnInsert = rendered.AsBsonDocument["$setOnInsert"].AsBsonDocument;
        return setOnInsert["InsertSequence"].ToInt64();
    }

    private static (MongoDbAggregatorPersistor persistor,
                    Mock<IMongoCollection<MongoDbAggregatorPersistor.AggregatorDocument>> collection,
                    Mock<IMessageTypeRegistry> registry)
        CreateMockedPersistor(
            Action<FindOptions<MongoDbAggregatorPersistor.AggregatorDocument,
                               MongoDbAggregatorPersistor.AggregatorDocument>>? captureOptions = null,
            Action<UpdateDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>? captureUpsert = null)
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

        mockCollection
            .SetupGet(c => c.Indexes)
            .Returns(mockIndexManager.Object);

        mockIndexManager
            .Setup(m => m.CreateManyAsync(
                It.IsAny<IEnumerable<CreateIndexModel<MongoDbAggregatorPersistor.AggregatorDocument>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // UpdateOneAsync (upsert) capture for the InsertDataAsync code path.
        mockCollection
            .Setup(c => c.UpdateOneAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<UpdateDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<UpdateOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>,
                      UpdateDefinition<MongoDbAggregatorPersistor.AggregatorDocument>,
                      UpdateOptions,
                      CancellationToken>(
                (_, update, _, _) => captureUpsert?.Invoke(update))
            .ReturnsAsync(new UpdateResult.Acknowledged(0, 1, null));

        // FindAsync: captures options (which embed the sort) and returns an empty cursor.
        var emptyDocs = new List<MongoDbAggregatorPersistor.AggregatorDocument>();
        var fakeCursor = new FakeAsyncCursor<MongoDbAggregatorPersistor.AggregatorDocument>(emptyDocs);

        mockCollection
            .Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<FindOptions<MongoDbAggregatorPersistor.AggregatorDocument, MongoDbAggregatorPersistor.AggregatorDocument>>(),
                It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<MongoDbAggregatorPersistor.AggregatorDocument>,
                      FindOptions<MongoDbAggregatorPersistor.AggregatorDocument, MongoDbAggregatorPersistor.AggregatorDocument>,
                      CancellationToken>(
                (_, opts, _) => captureOptions?.Invoke(opts))
            .ReturnsAsync(fakeCursor);

        var persistor = new MongoDbAggregatorPersistor(
            mockClient.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test-db" },
            Mock.Of<ILogger<MongoDbAggregatorPersistor>>(),
            registry.Object);

        return (persistor, mockCollection, registry);
    }

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
