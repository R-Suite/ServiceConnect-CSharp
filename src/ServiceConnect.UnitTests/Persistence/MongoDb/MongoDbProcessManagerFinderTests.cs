using System.Reflection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence.MongoDb;

[Collection("Mongo Bson serial")]
public class MongoDbProcessManagerFinderTests
{
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
        indexedCollections.TryAdd(MongoDbProcessManagerFinder.SanitizeCollectionName(typeof(TestProcessManagerData).FullName!), true);

        database.Setup(db => db.GetCollection<MongoDbData<TestProcessManagerData>>(
                MongoDbProcessManagerFinder.SanitizeCollectionName(typeof(TestProcessManagerData).FullName!),
                It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);

        collection.Setup(c => c.ReplaceOneAsync(
                It.IsAny<FilterDefinition<MongoDbData<TestProcessManagerData>>>(),
                It.IsAny<MongoDbData<TestProcessManagerData>>(),
                It.IsAny<ReplaceOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TestMongoException("boom"));

        await Assert.ThrowsAsync<PersistenceException>(() => finder.UpdateDataAsync(versionedData, CancellationToken.None));

        Assert.Equal(7L, versionedData.Version);
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
        indexedCollections.TryAdd(MongoDbProcessManagerFinder.SanitizeCollectionName(typeof(TestProcessManagerData).FullName!), true);

        database.Setup(db => db.GetCollection<MongoDbData<TestProcessManagerData>>(
                MongoDbProcessManagerFinder.SanitizeCollectionName(typeof(TestProcessManagerData).FullName!),
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

        Assert.Equal(11L, versionedData.Version);
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
        indexedCollections.TryAdd(MongoDbProcessManagerFinder.SanitizeCollectionName(typeof(TestProcessManagerData).FullName!), true);

        database.Setup(db => db.GetCollection<MongoDbData<TestProcessManagerData>>(
                MongoDbProcessManagerFinder.SanitizeCollectionName(typeof(TestProcessManagerData).FullName!),
                It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);

        collection.Setup(c => c.ReplaceOneAsync(
                It.IsAny<FilterDefinition<MongoDbData<TestProcessManagerData>>>(),
                It.IsAny<MongoDbData<TestProcessManagerData>>(),
                It.IsAny<ReplaceOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReplaceOneResult.Acknowledged(matchedCount: 1, modifiedCount: 1, upsertedId: null));

        await finder.UpdateDataAsync(versionedData, CancellationToken.None);

        Assert.Equal(5L, versionedData.Version);
    }

    [Fact]
    public async Task InsertDataAsync_ThrowsPersistenceException_WhenGenericMongoErrorOccurs()
    {
        // Generic MongoException (network failure, command error other than DuplicateKey, etc.)
        // surfaces as PersistenceException — the caller cannot recover via re-find.
        var finder = CreateFinder(out var database, out _);
        var collection = new Mock<IMongoCollection<MongoDbData<TestProcessManagerData>>>();
        var data = new TestProcessManagerData();

        var indexedCollections = (System.Collections.Concurrent.ConcurrentDictionary<string, bool>)typeof(MongoDbProcessManagerFinder)
            .GetField("_indexedCollections", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(finder)!;
        indexedCollections.TryAdd(MongoDbProcessManagerFinder.SanitizeCollectionName(typeof(TestProcessManagerData).FullName!), true);

        database.Setup(db => db.GetCollection<MongoDbData<TestProcessManagerData>>(
                MongoDbProcessManagerFinder.SanitizeCollectionName(typeof(TestProcessManagerData).FullName!),
                It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);

        collection.Setup(c => c.InsertOneAsync(
                It.IsAny<MongoDbData<TestProcessManagerData>>(),
                It.IsAny<InsertOneOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TestMongoException("network failure"));

        await Assert.ThrowsAsync<PersistenceException>(() => finder.InsertDataAsync(data, CancellationToken.None));
    }

    [Fact]
    public async Task InsertDataAsync_ThrowsConcurrencyException_WhenMongoWriteExceptionHasDuplicateKey()
    {
        // The unique CorrelationId index signals a concurrent first-message race for the
        // same saga: the loser's InsertOne raises MongoWriteException with
        // ServerErrorCategory.DuplicateKey. ProcessManagerProcessor's retry loop only
        // recovers from ConcurrencyException, so this code path must be rethrown as one.
        var finder = CreateFinder(out var database, out _);
        var collection = new Mock<IMongoCollection<MongoDbData<TestProcessManagerData>>>();
        var data = new TestProcessManagerData();

        var indexedCollections = (System.Collections.Concurrent.ConcurrentDictionary<string, bool>)typeof(MongoDbProcessManagerFinder)
            .GetField("_indexedCollections", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(finder)!;
        indexedCollections.TryAdd(MongoDbProcessManagerFinder.SanitizeCollectionName(typeof(TestProcessManagerData).FullName!), true);

        database.Setup(db => db.GetCollection<MongoDbData<TestProcessManagerData>>(
                MongoDbProcessManagerFinder.SanitizeCollectionName(typeof(TestProcessManagerData).FullName!),
                It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);

        var connectionId = new MongoDB.Driver.Core.Connections.ConnectionId(
            new MongoDB.Driver.Core.Servers.ServerId(
                new MongoDB.Driver.Core.Clusters.ClusterId(),
                new System.Net.DnsEndPoint("localhost", 27017)));
        // WriteError's constructor is internal in MongoDB.Driver 2.23.x; reflect into it
        // so the test doesn't depend on driver-internal accessibility decisions. The
        // production code under test only reads WriteError.Category, so the rest of the
        // properties stay at their default-constructed values.
        var writeErrorCtor = typeof(WriteError).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .First(c =>
            {
                var ps = c.GetParameters();
                return ps.Length >= 1 && ps[0].ParameterType == typeof(ServerErrorCategory);
            });
        var writeErrorArgs = writeErrorCtor.GetParameters().Select<System.Reflection.ParameterInfo, object?>(p => p.ParameterType switch
        {
            _ when p.ParameterType == typeof(ServerErrorCategory) => ServerErrorCategory.DuplicateKey,
            _ when p.ParameterType == typeof(int) => 11000,
            _ when p.ParameterType == typeof(string) => "E11000 duplicate key error: Data.CorrelationId_1",
            _ when p.ParameterType == typeof(MongoDB.Bson.BsonDocument) => new MongoDB.Bson.BsonDocument(),
            _ => p.HasDefaultValue ? p.DefaultValue : null
        }).ToArray();
        var writeError = (WriteError)writeErrorCtor.Invoke(writeErrorArgs);
        var dupEx = new MongoWriteException(connectionId, writeError, writeConcernError: null, innerException: null);

        collection.Setup(c => c.InsertOneAsync(
                It.IsAny<MongoDbData<TestProcessManagerData>>(),
                It.IsAny<InsertOneOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(dupEx);

        var thrown = await Assert.ThrowsAsync<ConcurrencyException>(
            () => finder.InsertDataAsync(data, CancellationToken.None));
        Assert.Same(dupEx, thrown.InnerException);
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
                MongoDbProcessManagerFinder.SanitizeCollectionName(typeof(TestProcessManagerData).FullName!),
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
        var expectedName = MongoDbProcessManagerFinder.SanitizeCollectionName(typeof(TestProcessManagerData).FullName!);

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
        // IMongoClient.Settings is read in the constructor to detect WriteConcern.Unacknowledged.
        // Return a default MongoClientSettings (WriteConcern.Acknowledged) so the mock doesn't NRE.
        client.SetupGet(c => c.Settings).Returns(new MongoClientSettings());

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
