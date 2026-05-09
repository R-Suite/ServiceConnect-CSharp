using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Verifies that EnsureIndexesAsync uses a single-flight semaphore so that
/// concurrent cold-start callers do not each fire CreateManyAsync.
/// Pre-fix: 8 concurrent callers all race past the Volatile.Read fast path and
/// each call CreateManyAsync. Post-fix: only one wins the semaphore; the others
/// re-check _indexed inside the lock and short-circuit.
/// </summary>
[Collection("Mongo Bson serial")]
public class MongoDbAggregatorPersistorIndexInitTests
{
    [Fact]
    public async Task EnsureIndexesAsync_ConcurrentColdStart_FiresCreateManyExactlyOnce()
    {
        // Use a gate TCS so that the first CreateManyAsync caller holds the channel
        // open long enough for all other concurrent callers to pile in. Without a
        // semaphore they all race past Volatile.Read(_indexed)==0 and each invoke
        // CreateManyAsync. With the semaphore only the winner enters; the rest block
        // and then see _indexed==1 on re-check.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var indexManager = new Mock<IMongoIndexManager<MongoDbAggregatorPersistor.AggregatorDocument>>();
        var createCount = 0;
        indexManager
            .Setup(m => m.CreateManyAsync(
                It.IsAny<IEnumerable<CreateIndexModel<MongoDbAggregatorPersistor.AggregatorDocument>>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref createCount);
                // Yield so other tasks can progress and hit EnsureIndexesAsync while
                // this call is "in flight", maximising the window for a race.
                await gate.Task.ConfigureAwait(false);
                return (IEnumerable<string>)["ok"];
            });

        var collection = new Mock<IMongoCollection<MongoDbAggregatorPersistor.AggregatorDocument>>();
        collection.SetupGet(c => c.Indexes).Returns(indexManager.Object);
        collection
            .Setup(c => c.InsertOneAsync(
                It.IsAny<MongoDbAggregatorPersistor.AggregatorDocument>(),
                It.IsAny<InsertOneOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<MongoDbAggregatorPersistor.AggregatorDocument>(
                It.IsAny<string>(), It.IsAny<MongoCollectionSettings?>()))
            .Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.SetupGet(c => c.Settings).Returns(new MongoClientSettings { WriteConcern = WriteConcern.W1 });
        client.Setup(c => c.GetDatabase(It.IsAny<string>(), It.IsAny<MongoDatabaseSettings?>()))
              .Returns(database.Object);

        var persistor = new MongoDbAggregatorPersistor(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "tests" },
            Mock.Of<ILogger<MongoDbAggregatorPersistor>>(),
            Mock.Of<IMessageTypeRegistry>());

        // Fire 8 concurrent inserts from separate thread-pool threads.
        // Task.Run ensures they run on distinct threads and can hit EnsureIndexesAsync
        // concurrently rather than sequentially on the same thread.
        const int concurrency = 8;
        var tasks = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(() => persistor.InsertDataAsync(
                new TestData { CorrelationId = Guid.NewGuid() }, "test")))
            .ToArray();

        // Let the tasks get started and queue up against EnsureIndexesAsync.
        await Task.Delay(50);

        // Release the gate so the in-flight CreateManyAsync (if any) can finish.
        gate.SetResult();

        await Task.WhenAll(tasks);

        Assert.Equal(1, createCount);
    }

    private sealed class TestData : IHasCorrelationId
    {
        public Guid CorrelationId { get; set; }
    }
}
