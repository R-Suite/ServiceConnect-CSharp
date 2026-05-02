using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoDbTimeoutStoreIndexMigrationTests
{
    static MongoDbTimeoutStoreIndexMigrationTests()
    {
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    private static string RenderKeys(IndexKeysDefinition<TimeoutData> keys) =>
        keys.Render(
            BsonSerializer.LookupSerializer<TimeoutData>(),
            BsonSerializer.SerializerRegistry).ToJson();

    private static (MongoDbTimeoutStore Store, Mock<IMongoIndexManager<TimeoutData>> Indexes)
        BuildStore(
            Action<Mock<IMongoIndexManager<TimeoutData>>>? indexSetup = null)
    {
        var indexes = new Mock<IMongoIndexManager<TimeoutData>>();

        // Defaults (applied before indexSetup so callers can override them).
        indexes.Setup(m => m.CreateManyAsync(
                It.IsAny<IEnumerable<CreateIndexModel<TimeoutData>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(["ok"]);
        indexes.Setup(m => m.DropOneAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Test-specific overrides applied after defaults.
        indexSetup?.Invoke(indexes);

        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.SetupGet(c => c.Indexes).Returns(indexes.Object);
        collection.Setup(c => c.InsertOneAsync(
                It.IsAny<TimeoutData>(),
                It.IsAny<InsertOneOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<TimeoutData>("Timeouts", null))
            .Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);

        var store = new MongoDbTimeoutStore(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test" },
            NullLogger<MongoDbTimeoutStore>.Instance);

        return (store, indexes);
    }

    [Fact]
    public async Task EnsureTimeoutIndex_CreatesTimeLockedCompoundAndLockExpiresAtSingle()
    {
        // The pre-Phase-9 single (Time, Locked, LockExpiresAt) compound was rejected
        // by real MongoDB with code 171 ("cannot index parallel arrays") because the
        // C# driver serialises DateTimeOffset as a 2-element BSON array, and a
        // compound spanning two array-typed fields trips that rule. The same applies
        // to a hypothetical (Time, LockExpiresAt) — both fields are DateTimeOffset.
        //
        // The fix uses (Time, Locked) — one array + one scalar, OK — for the
        // Locked == false branch of the due filter (with Time as the sort prefix),
        // plus a single-field (LockExpiresAt) index for the LockExpiresAt <= utcNow
        // branch. Single array-valued fields are fine; only multi-array compounds
        // are rejected.
        IEnumerable<CreateIndexModel<TimeoutData>>? captured = null;
        var (store, _) = BuildStore(indexes =>
        {
            indexes.Setup(m => m.CreateManyAsync(
                    It.IsAny<IEnumerable<CreateIndexModel<TimeoutData>>>(),
                    It.IsAny<CancellationToken>()))
                .Callback<IEnumerable<CreateIndexModel<TimeoutData>>, CancellationToken>((m, _) => captured = [.. m])
                .ReturnsAsync(["ok"]);
        });

        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = Guid.NewGuid(),
            Destination = "dest",
            ProcessManagerId = Guid.NewGuid(),
            Time = DateTimeOffset.UtcNow,
            Headers = new Dictionary<string, object>(StringComparer.Ordinal),
        });

        Assert.NotNull(captured);
        var keysJsonList = captured!.Select(m => RenderKeys(m.Keys)).ToList();

        // (Time, Locked) — covers the Locked == false branch of the due filter.
        Assert.Contains(keysJsonList, k =>
            k.Contains("\"Time\" : 1") && k.Contains("\"Locked\" : 1") && !k.Contains("\"LockExpiresAt\""));

        // Single-field (LockExpiresAt) — covers the LockExpiresAt <= utcNow branch.
        Assert.Contains(keysJsonList, k =>
            k.Contains("\"LockExpiresAt\" : 1") && !k.Contains("\"Time\"") && !k.Contains("\"Locked\" :"));

        // No multi-array compound — that's the parallel-arrays trap.
        Assert.DoesNotContain(keysJsonList, k =>
            k.Contains("\"Time\" : 1") && k.Contains("\"LockExpiresAt\" : 1"));
    }

    [Fact]
    public async Task EnsureTimeoutIndex_DropsLegacyLockedTimeIndex()
    {
        var (store, indexes) = BuildStore();

        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = Guid.NewGuid(),
            Destination = "dest",
            ProcessManagerId = Guid.NewGuid(),
            Time = DateTimeOffset.UtcNow,
            Headers = new Dictionary<string, object>(StringComparer.Ordinal),
        });

        indexes.Verify(
            m => m.DropOneAsync("Locked_1_Time_1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EnsureTimeoutIndex_SwallowsIndexNotFoundOnDrop()
    {
        var (store, _) = BuildStore(indexes =>
        {
            // Code 27 = IndexNotFound.
            var connectionId = new MongoDB.Driver.Core.Connections.ConnectionId(
                new MongoDB.Driver.Core.Servers.ServerId(
                    new MongoDB.Driver.Core.Clusters.ClusterId(),
                    new System.Net.DnsEndPoint("localhost", 27017)));
            var result = new BsonDocument { ["ok"] = 0, ["code"] = 27, ["errmsg"] = "index not found" };
            var command = new BsonDocument { ["dropIndexes"] = "Timeouts" };
            indexes.Setup(m => m.DropOneAsync("Locked_1_Time_1", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new MongoCommandException(connectionId, "index not found", command, result));
        });

        // Should NOT throw.
        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = Guid.NewGuid(),
            Destination = "dest",
            ProcessManagerId = Guid.NewGuid(),
            Time = DateTimeOffset.UtcNow,
            Headers = new Dictionary<string, object>(StringComparer.Ordinal),
        });
    }

    [Fact]
    public async Task EnsureTimeoutIndex_PropagatesOtherDropErrors()
    {
        var (store, _) = BuildStore(indexes =>
        {
            // Code 13 = Unauthorized.
            var connectionId = new MongoDB.Driver.Core.Connections.ConnectionId(
                new MongoDB.Driver.Core.Servers.ServerId(
                    new MongoDB.Driver.Core.Clusters.ClusterId(),
                    new System.Net.DnsEndPoint("localhost", 27017)));
            var result = new BsonDocument { ["ok"] = 0, ["code"] = 13, ["errmsg"] = "unauthorized" };
            var command = new BsonDocument { ["dropIndexes"] = "Timeouts" };
            indexes.Setup(m => m.DropOneAsync("Locked_1_Time_1", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new MongoCommandException(connectionId, "unauthorized", command, result));
        });

        await Assert.ThrowsAsync<PersistenceException>(() =>
            store.InsertTimeoutAsync(new TimeoutData
            {
                Id = Guid.NewGuid(),
                Destination = "dest",
                ProcessManagerId = Guid.NewGuid(),
                Time = DateTimeOffset.UtcNow,
                Headers = new Dictionary<string, object>(StringComparer.Ordinal),
            }));
    }
}
