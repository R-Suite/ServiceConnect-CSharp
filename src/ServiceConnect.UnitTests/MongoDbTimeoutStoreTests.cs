using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Configuration;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoDbTimeoutStoreTests
{
    [Fact]
    public void BuildDueTimeoutFilter_IncludesExpiredLeasesForRecovery()
    {
        var now = new DateTimeOffset(2026, 4, 15, 8, 0, 0, TimeSpan.Zero);

        var filter = MongoDbTimeoutStore.BuildDueTimeoutFilter(now);
        var rendered = filter.Render(
            BsonSerializer.LookupSerializer<TimeoutData>(),
            BsonSerializer.SerializerRegistry);

        var json = rendered.ToJson();

        Assert.Contains("\"Time\"", json);
        Assert.Contains("\"$or\"", json);
        Assert.Contains("\"Locked\" : false", json);
        Assert.Contains("\"LockExpiresAt\"", json);
        Assert.Contains("\"$lte\"", json);
    }

    private static MongoCommandException MakeMongoCommandException(int code)
    {
        var connectionId = new ConnectionId(
            new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017)));
        var result = new BsonDocument
        {
            ["ok"] = 0,
            ["code"] = code,
            ["errmsg"] = $"index conflict (code {code})",
        };
        var command = new BsonDocument { ["createIndexes"] = "Timeouts" };
        return new MongoCommandException(connectionId, $"command failed with code {code}", command, result);
    }

    private static (MongoDbTimeoutStore Store, Mock<IMongoCollection<TimeoutData>> Collection)
        BuildStoreWithIndexException(int? throwCode)
    {
        var indexes = new Mock<IMongoIndexManager<TimeoutData>>();
        var indexSetup = indexes.Setup(m => m.CreateManyAsync(
            It.IsAny<IEnumerable<CreateIndexModel<TimeoutData>>>(),
            It.IsAny<CancellationToken>()));
        if (throwCode.HasValue)
            indexSetup.ThrowsAsync(MakeMongoCommandException(throwCode.Value));
        else
            indexSetup.ReturnsAsync(new List<string> { "ok" });

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

        return (store, collection);
    }

    [Fact]
    public async Task InsertTimeout_IndexConflictCode85_IsTolerated_AndInsertProceeds()
    {
        var (store, collection) = BuildStoreWithIndexException(85);

        var ex = await Record.ExceptionAsync(() =>
            store.InsertTimeoutAsync(new TimeoutData { Id = Guid.NewGuid(), Time = DateTimeOffset.UtcNow }));

        Assert.Null(ex);
        collection.Verify(c => c.InsertOneAsync(
                It.IsAny<TimeoutData>(),
                It.IsAny<InsertOneOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InsertTimeout_IndexConflictCode86_IsTolerated_AndInsertProceeds()
    {
        var (store, collection) = BuildStoreWithIndexException(86);

        var ex = await Record.ExceptionAsync(() =>
            store.InsertTimeoutAsync(new TimeoutData { Id = Guid.NewGuid(), Time = DateTimeOffset.UtcNow }));

        Assert.Null(ex);
        collection.Verify(c => c.InsertOneAsync(
                It.IsAny<TimeoutData>(),
                It.IsAny<InsertOneOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InsertTimeout_OtherMongoCommandException_PropagatesAsPersistenceException()
    {
        var (store, _) = BuildStoreWithIndexException(13); // Unauthorized — must NOT be swallowed.

        await Assert.ThrowsAsync<PersistenceException>(() =>
            store.InsertTimeoutAsync(new TimeoutData { Id = Guid.NewGuid(), Time = DateTimeOffset.UtcNow }));
    }
}
