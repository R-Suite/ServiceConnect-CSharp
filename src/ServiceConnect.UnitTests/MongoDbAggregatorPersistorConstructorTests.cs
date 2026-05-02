using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MongoDB.Driver;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoDbAggregatorPersistorConstructorTests
{
    static MongoDbAggregatorPersistorConstructorTests()
    {
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var client = new Mock<IMongoClient>();
        var typeRegistry = new Mock<IMessageTypeRegistry>();
        Assert.Throws<ArgumentNullException>(() =>
            new MongoDbAggregatorPersistor(
                client.Object,
                new MongoDbPersistenceOptions { DatabaseName = "test" },
                logger: null!,
                typeRegistry.Object));
    }

    [Fact]
    public void Constructor_NullTypeRegistry_ThrowsArgumentNullException()
    {
        // Regression guard for the existing null-check that already exists.
        var client = new Mock<IMongoClient>();
        Assert.Throws<ArgumentNullException>(() =>
            new MongoDbAggregatorPersistor(
                client.Object,
                new MongoDbPersistenceOptions { DatabaseName = "test" },
                NullLogger<MongoDbAggregatorPersistor>.Instance,
                typeRegistry: null!));
    }
}
