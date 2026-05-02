using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Verifies the sort shape and monotonic InsertSequence behaviour introduced in M36.
/// </summary>
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
            .Render(
                BsonSerializer.LookupSerializer<MongoDbAggregatorPersistor.AggregatorDocument>(),
                BsonSerializer.SerializerRegistry)
            .ToJson();

        Assert.Contains("\"InsertedAtTicks\" : 1", rendered);
        Assert.Contains("\"InsertSequence\" : 1", rendered);
        // Id is the final cross-process tie-break; verify it is also present.
        Assert.Contains("\"_id\" : 1", rendered);
    }

    [Fact]
    public async Task InsertData_AssignsMonotonicInsertSequence()
    {
        // Capture every document passed to InsertOneAsync so we can verify the assigned
        // InsertSequence values increase monotonically across consecutive inserts.
        var captured = new List<MongoDbAggregatorPersistor.AggregatorDocument>();

        var (persistor, _, _) = CreateMockedPersistor(captureInsert: captured.Add);

        await persistor.InsertDataAsync(new { Value = "first" }, "agg");
        await persistor.InsertDataAsync(new { Value = "second" }, "agg");

        Assert.Equal(2, captured.Count);
        Assert.True(
            captured[1].InsertSequence > captured[0].InsertSequence,
            $"Expected InsertSequence to be monotonically increasing; got {captured[0].InsertSequence} then {captured[1].InsertSequence}");
    }

    private static (MongoDbAggregatorPersistor persistor,
                    Mock<IMongoCollection<MongoDbAggregatorPersistor.AggregatorDocument>> collection,
                    Mock<IMessageTypeRegistry> registry)
        CreateMockedPersistor(
            Action<FindOptions<MongoDbAggregatorPersistor.AggregatorDocument,
                               MongoDbAggregatorPersistor.AggregatorDocument>>? captureOptions = null,
            Action<MongoDbAggregatorPersistor.AggregatorDocument>? captureInsert = null)
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

        mockCollection
            .SetupGet(c => c.Indexes)
            .Returns(mockIndexManager.Object);

        mockIndexManager
            .Setup(m => m.CreateManyAsync(
                It.IsAny<IEnumerable<CreateIndexModel<MongoDbAggregatorPersistor.AggregatorDocument>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        // InsertOneAsync capture
        mockCollection
            .Setup(c => c.InsertOneAsync(
                It.IsAny<MongoDbAggregatorPersistor.AggregatorDocument>(),
                It.IsAny<InsertOneOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<MongoDbAggregatorPersistor.AggregatorDocument, InsertOneOptions, CancellationToken>(
                (doc, _, _) => captureInsert?.Invoke(doc))
            .Returns(Task.CompletedTask);

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
