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

    private static MongoDbTimeoutStore BuildStore(Mock<IMongoCollection<TimeoutData>> collection)
    {
        // EnsureTimeoutIndexAsync runs on every read/write path, so every store under
        // test needs a benign Indexes mock — otherwise the collection mock returns null and
        // CreateManyAsync throws NullReferenceException unrelated to what the test is probing.
        if (collection.Object.Indexes == null)
        {
            var indexes = new Mock<IMongoIndexManager<TimeoutData>>();
            indexes.Setup(m => m.CreateManyAsync(
                    It.IsAny<IEnumerable<CreateIndexModel<TimeoutData>>>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<string> { "ok" });
            collection.SetupGet(c => c.Indexes).Returns(indexes.Object);
        }

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<TimeoutData>("Timeouts", null))
            .Returns(collection.Object);

        var client = new Mock<IMongoClient>();
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);

        return new MongoDbTimeoutStore(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test" },
            NullLogger<MongoDbTimeoutStore>.Instance);
    }

    private static string RenderFilter(FilterDefinition<TimeoutData> filter)
    {
        return filter.Render(
                BsonSerializer.LookupSerializer<TimeoutData>(),
                BsonSerializer.SerializerRegistry)
            .ToJson();
    }

    private static string RenderUpdate(UpdateDefinition<TimeoutData> update)
    {
        return update.Render(
                BsonSerializer.LookupSerializer<TimeoutData>(),
                BsonSerializer.SerializerRegistry)
            .ToJson();
    }

    private static DeleteResult BuildDeleteResult(long deletedCount)
    {
        var result = new Mock<DeleteResult>();
        result.SetupGet(r => r.IsAcknowledged).Returns(true);
        result.SetupGet(r => r.DeletedCount).Returns(deletedCount);
        return result.Object;
    }

    private static UpdateResult BuildUpdateResult(long matchedCount, long modifiedCount)
    {
        var result = new Mock<UpdateResult>();
        result.SetupGet(r => r.IsAcknowledged).Returns(true);
        result.SetupGet(r => r.MatchedCount).Returns(matchedCount);
        result.SetupGet(r => r.ModifiedCount).Returns(modifiedCount);
        result.SetupGet(r => r.UpsertedId).Returns((BsonValue)BsonNull.Value);
        return result.Object;
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

    [Fact]
    public async Task RemoveDispatchedTimeout_WithMatchingOwner_DeletesLockedTimeout()
    {
        var id = Guid.NewGuid();
        var lockOwner = Guid.NewGuid();
        FilterDefinition<TimeoutData>? capturedFilter = null;

        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.Setup(c => c.DeleteOneAsync(It.IsAny<FilterDefinition<TimeoutData>>(), It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<TimeoutData>, CancellationToken>((filter, _) => capturedFilter = filter)
            .ReturnsAsync(BuildDeleteResult(1));

        var store = BuildStore(collection);

        await ((ILeaseAwareTimeoutStore)store).RemoveDispatchedTimeoutAsync(id, lockOwner);

        var json = RenderFilter(Assert.IsAssignableFrom<FilterDefinition<TimeoutData>>(capturedFilter));
        Assert.Contains("\"_id\"", json);
        Assert.Contains(id.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"Locked\" : true", json);
        Assert.Contains("\"LockedBy\"", json);
        Assert.Contains(lockOwner.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseDispatchedTimeout_WithMatchingOwner_ClearsLockFields()
    {
        var id = Guid.NewGuid();
        var lockOwner = Guid.NewGuid();
        FilterDefinition<TimeoutData>? capturedFilter = null;
        UpdateDefinition<TimeoutData>? capturedUpdate = null;

        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.Setup(c => c.UpdateOneAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<UpdateDefinition<TimeoutData>>(),
                It.IsAny<UpdateOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<FilterDefinition<TimeoutData>, UpdateDefinition<TimeoutData>, UpdateOptions?, CancellationToken>((filter, update, _, _) =>
            {
                capturedFilter = filter;
                capturedUpdate = update;
            })
            .ReturnsAsync(BuildUpdateResult(1, 1));

        var store = BuildStore(collection);

        await ((ILeaseAwareTimeoutStore)store).ReleaseDispatchedTimeoutAsync(id, lockOwner);

        var filterJson = RenderFilter(Assert.IsAssignableFrom<FilterDefinition<TimeoutData>>(capturedFilter));
        Assert.Contains("\"_id\"", filterJson);
        Assert.Contains(id.ToString(), filterJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"Locked\" : true", filterJson);
        Assert.Contains("\"LockedBy\"", filterJson);
        Assert.Contains(lockOwner.ToString(), filterJson, StringComparison.OrdinalIgnoreCase);

        var updateJson = RenderUpdate(Assert.IsAssignableFrom<UpdateDefinition<TimeoutData>>(capturedUpdate));
        Assert.Contains("\"Locked\" : false", updateJson);
        Assert.Contains("\"LockedBy\"", updateJson);
        Assert.Contains(Guid.Empty.ToString(), updateJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"LockExpiresAt\" : null", updateJson);
    }

    [Fact]
    public async Task RemoveDispatchedTimeout_WithStaleOwner_IsBenignNoOp()
    {
        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.Setup(c => c.DeleteOneAsync(It.IsAny<FilterDefinition<TimeoutData>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildDeleteResult(0));

        var store = BuildStore(collection);

        var exception = await Record.ExceptionAsync(() =>
            ((ILeaseAwareTimeoutStore)store).RemoveDispatchedTimeoutAsync(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Null(exception);
    }

    [Fact]
    public async Task RemoveDispatchedTimeout_WithMatchingOwner_WhenMongoFails_IncludesLockOwnerInPersistenceException()
    {
        var id = Guid.NewGuid();
        var lockOwner = Guid.NewGuid();
        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.Setup(c => c.DeleteOneAsync(It.IsAny<FilterDefinition<TimeoutData>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(MakeMongoCommandException(91));

        var store = BuildStore(collection);

        var exception = await Assert.ThrowsAsync<PersistenceException>(() =>
            ((ILeaseAwareTimeoutStore)store).RemoveDispatchedTimeoutAsync(id, lockOwner));

        Assert.Contains(id.ToString(), exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(lockOwner.ToString(), exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseDispatchedTimeout_WithStaleOwner_IsBenignNoOp()
    {
        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.Setup(c => c.UpdateOneAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<UpdateDefinition<TimeoutData>>(),
                It.IsAny<UpdateOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildUpdateResult(0, 0));

        var store = BuildStore(collection);

        var exception = await Record.ExceptionAsync(() =>
            ((ILeaseAwareTimeoutStore)store).ReleaseDispatchedTimeoutAsync(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Null(exception);
    }

    [Fact]
    public async Task ReleaseDispatchedTimeout_WithMatchingOwner_WhenMongoFails_IncludesLockOwnerInPersistenceException()
    {
        var id = Guid.NewGuid();
        var lockOwner = Guid.NewGuid();
        var collection = new Mock<IMongoCollection<TimeoutData>>();
        collection.Setup(c => c.UpdateOneAsync(
                It.IsAny<FilterDefinition<TimeoutData>>(),
                It.IsAny<UpdateDefinition<TimeoutData>>(),
                It.IsAny<UpdateOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(MakeMongoCommandException(91));

        var store = BuildStore(collection);

        var exception = await Assert.ThrowsAsync<PersistenceException>(() =>
            ((ILeaseAwareTimeoutStore)store).ReleaseDispatchedTimeoutAsync(id, lockOwner));

        Assert.Contains(id.ToString(), exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(lockOwner.ToString(), exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
