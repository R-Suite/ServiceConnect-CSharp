using System.Reflection;
using Microsoft.Extensions.Logging;
using Moq;
using MongoDB.Driver;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoDbProcessManagerFinderTests
{
    [Fact]
    public async Task InsertDataAsync_UnwrapsTargetInvocationException()
    {
        var finder = CreateFinder(out _, out _);
        var cacheField = typeof(MongoDbProcessManagerFinder)
            .GetField("InsertDelegateCache", BindingFlags.Static | BindingFlags.NonPublic)!;
        var cache = (System.Collections.IDictionary)cacheField.GetValue(null)!;
        var data = new TestProcessManagerData();
        var inner = new InvalidOperationException("inner");

        cache[data.GetType()] = new Func<MongoDbProcessManagerFinder, IProcessManagerData, string, CancellationToken, Task>(
            (_, _, _, _) => throw new TargetInvocationException(inner));

        try
        {
            var exception = await Record.ExceptionAsync(() => finder.InsertDataAsync(data, CancellationToken.None));

            Assert.Same(inner, exception);
        }
        finally
        {
            cache.Remove(data.GetType());
        }
    }

    [Fact]
    public async Task UpdateDataAsync_RestoresOriginalVersion_WhenReplaceFails()
    {
        var finder = CreateFinder(out var database, out _);
        var collection = new Mock<IMongoCollection<MongoDbData<TestProcessManagerData>>>();
        var versionedData = new MongoDbData<TestProcessManagerData>
        {
            Id = Guid.NewGuid(),
            Version = 7,
            Data = new TestProcessManagerData()
        };

        var indexedCollections = (System.Collections.Concurrent.ConcurrentDictionary<string, bool>)typeof(MongoDbProcessManagerFinder)
            .GetField("_indexedCollections", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(finder)!;
        indexedCollections.TryAdd(typeof(TestProcessManagerData).FullName!, true);

        database.Setup(db => db.GetCollection<MongoDbData<TestProcessManagerData>>(
                typeof(TestProcessManagerData).FullName,
                It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);

        collection.Setup(c => c.ReplaceOneAsync(
                It.IsAny<FilterDefinition<MongoDbData<TestProcessManagerData>>>(),
                It.IsAny<MongoDbData<TestProcessManagerData>>(),
                It.IsAny<ReplaceOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TestMongoException("boom"));

        await Assert.ThrowsAsync<PersistenceException>(() => finder.UpdateDataAsync(versionedData, CancellationToken.None));

        Assert.Equal(7, versionedData.Version);
    }

    [Fact]
    public async Task UpdateDataAsync_WhenCancelled_DoesNotBumpCallerVersion()
    {
        // The caller's version must only advance on a confirmed successful write.
        // If ReplaceOneAsync is cancelled (OperationCanceledException), the caller's
        // instance must still carry the original version so a retry targets the
        // right row and does not see itself as ahead of the stored document.
        var finder = CreateFinder(out var database, out _);
        var collection = new Mock<IMongoCollection<MongoDbData<TestProcessManagerData>>>();
        var versionedData = new MongoDbData<TestProcessManagerData>
        {
            Id = Guid.NewGuid(),
            Version = 11,
            Data = new TestProcessManagerData()
        };

        var indexedCollections = (System.Collections.Concurrent.ConcurrentDictionary<string, bool>)typeof(MongoDbProcessManagerFinder)
            .GetField("_indexedCollections", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(finder)!;
        indexedCollections.TryAdd(typeof(TestProcessManagerData).FullName!, true);

        database.Setup(db => db.GetCollection<MongoDbData<TestProcessManagerData>>(
                typeof(TestProcessManagerData).FullName,
                It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);

        collection.Setup(c => c.ReplaceOneAsync(
                It.IsAny<FilterDefinition<MongoDbData<TestProcessManagerData>>>(),
                It.IsAny<MongoDbData<TestProcessManagerData>>(),
                It.IsAny<ReplaceOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => finder.UpdateDataAsync(versionedData, CancellationToken.None));

        Assert.Equal(11, versionedData.Version);
    }

    [Fact]
    public async Task UpdateDataAsync_OnSuccess_BumpsCallerVersion()
    {
        var finder = CreateFinder(out var database, out _);
        var collection = new Mock<IMongoCollection<MongoDbData<TestProcessManagerData>>>();
        var versionedData = new MongoDbData<TestProcessManagerData>
        {
            Id = Guid.NewGuid(),
            Version = 4,
            Data = new TestProcessManagerData()
        };

        var indexedCollections = (System.Collections.Concurrent.ConcurrentDictionary<string, bool>)typeof(MongoDbProcessManagerFinder)
            .GetField("_indexedCollections", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(finder)!;
        indexedCollections.TryAdd(typeof(TestProcessManagerData).FullName!, true);

        database.Setup(db => db.GetCollection<MongoDbData<TestProcessManagerData>>(
                typeof(TestProcessManagerData).FullName,
                It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);

        collection.Setup(c => c.ReplaceOneAsync(
                It.IsAny<FilterDefinition<MongoDbData<TestProcessManagerData>>>(),
                It.IsAny<MongoDbData<TestProcessManagerData>>(),
                It.IsAny<ReplaceOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReplaceOneResult.Acknowledged(matchedCount: 1, modifiedCount: 1, upsertedId: null));

        await finder.UpdateDataAsync(versionedData, CancellationToken.None);

        Assert.Equal(5, versionedData.Version);
    }

    [Fact]
    public async Task InsertDataAsync_ThrowsPersistenceException_WhenDuplicateCorrelationIdExists()
    {
        var finder = CreateFinder(out var database, out _);
        var collection = new Mock<IMongoCollection<MongoDbData<TestProcessManagerData>>>();
        var data = new TestProcessManagerData();

        var indexedCollections = (System.Collections.Concurrent.ConcurrentDictionary<string, bool>)typeof(MongoDbProcessManagerFinder)
            .GetField("_indexedCollections", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(finder)!;
        indexedCollections.TryAdd(typeof(TestProcessManagerData).FullName!, true);

        database.Setup(db => db.GetCollection<MongoDbData<TestProcessManagerData>>(
                typeof(TestProcessManagerData).FullName,
                It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);

        collection.Setup(c => c.InsertOneAsync(
                It.IsAny<MongoDbData<TestProcessManagerData>>(),
                It.IsAny<InsertOneOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TestMongoException("duplicate"));

        await Assert.ThrowsAsync<PersistenceException>(() => finder.InsertDataAsync(data, CancellationToken.None));
    }

    [Fact]
    public async Task InsertDataAsync_CreatesUniqueCorrelationIdIndex()
    {
        var finder = CreateFinder(out var database, out _);
        var collection = new Mock<IMongoCollection<MongoDbData<TestProcessManagerData>>>();
        var indexManager = new Mock<IMongoIndexManager<MongoDbData<TestProcessManagerData>>>();
        var data = new TestProcessManagerData();
        CreateIndexModel<MongoDbData<TestProcessManagerData>>? capturedIndexModel = null;

        database.Setup(db => db.GetCollection<MongoDbData<TestProcessManagerData>>(
                typeof(TestProcessManagerData).FullName,
                It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);

        collection.SetupGet(c => c.Indexes)
            .Returns(indexManager.Object);

        indexManager.Setup(m => m.CreateOneAsync(
                It.IsAny<CreateIndexModel<MongoDbData<TestProcessManagerData>>>(),
                It.IsAny<CreateOneIndexOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<CreateIndexModel<MongoDbData<TestProcessManagerData>>, CreateOneIndexOptions, CancellationToken>(
                (model, _, _) => capturedIndexModel = model)
            .ReturnsAsync("Data.CorrelationId_1");

        collection.Setup(c => c.InsertOneAsync(
                It.IsAny<MongoDbData<TestProcessManagerData>>(),
                It.IsAny<InsertOneOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await finder.InsertDataAsync(data, CancellationToken.None);

        Assert.NotNull(capturedIndexModel);
        Assert.True(capturedIndexModel!.Options?.Unique);
    }

    [Fact]
    public async Task InsertDataAsync_UsesFullyQualifiedTypeNameForCollection()
    {
        var finder = CreateFinder(out var database, out _);
        var collection = new Mock<IMongoCollection<MongoDbData<TestProcessManagerData>>>();
        var data = new TestProcessManagerData();
        var expectedName = typeof(TestProcessManagerData).FullName!;

        var indexedCollections = (System.Collections.Concurrent.ConcurrentDictionary<string, bool>)typeof(MongoDbProcessManagerFinder)
            .GetField("_indexedCollections", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(finder)!;
        indexedCollections.TryAdd(expectedName, true);

        // Short-name-only Setup must NOT match — the finder must ask for the full name.
        database.Setup(db => db.GetCollection<MongoDbData<TestProcessManagerData>>(
                nameof(TestProcessManagerData),
                It.IsAny<MongoCollectionSettings>()))
            .Throws(new InvalidOperationException("collection resolution used short name"));

        string? requestedName = null;
        database.Setup(db => db.GetCollection<MongoDbData<TestProcessManagerData>>(
                It.IsAny<string>(),
                It.IsAny<MongoCollectionSettings>()))
            .Callback<string, MongoCollectionSettings>((name, _) => requestedName = name)
            .Returns(collection.Object);

        collection.Setup(c => c.InsertOneAsync(
                It.IsAny<MongoDbData<TestProcessManagerData>>(),
                It.IsAny<InsertOneOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await finder.InsertDataAsync(data, CancellationToken.None);

        Assert.Equal(expectedName, requestedName);
        Assert.Contains('.', requestedName!);
    }

    private static MongoDbProcessManagerFinder CreateFinder(
        out Mock<IMongoDatabase> database,
        out Mock<IMongoClient> client)
    {
        database = new Mock<IMongoDatabase>();
        client = new Mock<IMongoClient>();
        client.Setup(c => c.GetDatabase("test-db", It.IsAny<MongoDatabaseSettings>()))
            .Returns(database.Object);

        return new MongoDbProcessManagerFinder(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test-db" },
            Mock.Of<ILogger<MongoDbProcessManagerFinder>>());
    }

    private sealed class TestMongoException(string message) : MongoException(message);

    public sealed class TestProcessManagerData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; } = Guid.NewGuid();
    }
}
