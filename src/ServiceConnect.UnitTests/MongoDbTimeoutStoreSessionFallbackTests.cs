using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoDbTimeoutStoreSessionFallbackTests
{
    static MongoDbTimeoutStoreSessionFallbackTests()
    {
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    [Fact]
    public async Task GetTimeoutsBatch_StartSessionThrowsMongoConfiguration_FallsBackAndLogs()
    {
        // Logger-capture pattern matching project canon: Mock<ILogger<T>> + IsEnabled(true) +
        // InvocationAction + DynamicInvoke.
        var logEntries = new List<(LogLevel Level, string Message, Exception? Exception)>();
        var logger = new Mock<ILogger<MongoDbTimeoutStore>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        logger.Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
            {
                var level = (LogLevel)invocation.Arguments[0];
                var state = invocation.Arguments[2];
                var exception = (Exception?)invocation.Arguments[3];
                var formatter = (Delegate)invocation.Arguments[4];
                var message = (string)formatter.DynamicInvoke(state, exception)!;
                logEntries.Add((level, message, exception));
            }));

        var indexes = new Mock<IMongoIndexManager<TimeoutData>>();
        indexes.Setup(m => m.CreateManyAsync(It.IsAny<IEnumerable<CreateIndexModel<TimeoutData>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["ok"]);
        indexes.Setup(m => m.DropOneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.SetupGet(c => c.Indexes).Returns(indexes.Object);

        // UpdateMany without session (the fallback path) is exercised — set it up to
        // succeed with zero matches so the FindAsync candidate query also fires.
        collection.Setup(c => c.UpdateManyAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<UpdateDefinition<TimeoutData>>(),
                It.IsAny<UpdateOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateResult.Acknowledged(0, 0, null));

        var emptyGuidCursor = new Mock<IAsyncCursor<Guid>>();
        emptyGuidCursor.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        emptyGuidCursor.SetupGet(c => c.Current).Returns([]);
        collection.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<FindOptions<TimeoutData, Guid>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyGuidCursor.Object);

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<TimeoutData>("Timeouts", null)).Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.SetupGet(c => c.Settings).Returns(new MongoClientSettings { WriteConcern = WriteConcern.W1 });
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);
        // Throw a non-NotSupportedException MongoException to verify the broadened fallback.
        client.Setup(c => c.StartSessionAsync(It.IsAny<ClientSessionOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MongoConfigurationException("test cluster is misconfigured"));

        var store = new MongoDbTimeoutStore(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test" },
            logger.Object);

        // Should not throw — falls through to the unsessioned UpdateMany/FindAsync path.
        var batch = await store.GetTimeoutsBatchAsync();

        Assert.Empty(batch.DueTimeouts);
        Assert.Contains(logEntries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("session", StringComparison.OrdinalIgnoreCase) &&
            e.Exception is MongoConfigurationException);
    }
}
