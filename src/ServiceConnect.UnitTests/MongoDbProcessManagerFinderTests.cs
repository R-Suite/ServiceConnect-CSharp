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
        indexedCollections.TryAdd(nameof(TestProcessManagerData), true);

        database.Setup(db => db.GetCollection<MongoDbData<TestProcessManagerData>>(
                nameof(TestProcessManagerData),
                It.IsAny<MongoCollectionSettings>()))
            .Returns(collection.Object);

        collection.Setup(c => c.ReplaceOneAsync(
                It.IsAny<FilterDefinition<MongoDbData<TestProcessManagerData>>>(),
                versionedData,
                It.IsAny<ReplaceOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TestMongoException("boom"));

        await Assert.ThrowsAsync<PersistenceException>(() => finder.UpdateDataAsync(versionedData, CancellationToken.None));

        Assert.Equal(7, versionedData.Version);
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
