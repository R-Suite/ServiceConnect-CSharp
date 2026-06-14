using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MongoDB.Driver;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence.MongoDb;

[Collection("Mongo Bson serial")]
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

    [Fact]
    public void Constructor_RejectsUnacknowledgedWriteConcern()
    {
        var settings = MongoClientSettings.FromConnectionString("mongodb://localhost:27017");
        settings.WriteConcern = WriteConcern.Unacknowledged;
        var client = new MongoClient(settings);

        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = "mongodb://localhost:27017",
            DatabaseName = "tests",
        };

        var typeRegistry = new Mock<IMessageTypeRegistry>().Object;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new MongoDbAggregatorPersistor(client, options, NullLogger<MongoDbAggregatorPersistor>.Instance, typeRegistry));
        Assert.Contains("acknowledged WriteConcern", ex.Message);
    }

    [Fact]
    public void Constructor_AcceptsAcknowledgedWriteConcern()
    {
        var settings = MongoClientSettings.FromConnectionString("mongodb://localhost:27017");
        settings.WriteConcern = WriteConcern.Acknowledged;
        var client = new MongoClient(settings);

        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = "mongodb://localhost:27017",
            DatabaseName = "tests",
        };

        var typeRegistry = new Mock<IMessageTypeRegistry>().Object;

        _ = new MongoDbAggregatorPersistor(client, options, NullLogger<MongoDbAggregatorPersistor>.Instance, typeRegistry);
    }
}
