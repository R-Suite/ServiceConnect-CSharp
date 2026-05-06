using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoDbTimeoutStoreConstructorTests
{
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

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new MongoDbTimeoutStore(client, options, NullLogger<MongoDbTimeoutStore>.Instance));
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

        // Should not throw — construction must succeed for acknowledged writes.
        _ = new MongoDbTimeoutStore(client, options, NullLogger<MongoDbTimeoutStore>.Instance);
    }
}
