using Moq;
using MongoDB.Driver;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

[Collection("Mongo Bson serial")]
public class MongoDbProcessManagerFinderConstructorTests
{
    static MongoDbProcessManagerFinderConstructorTests()
    {
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    private static Mock<IMongoClient> BuildAcknowledgedClient()
    {
        var client = new Mock<IMongoClient>();
        client.SetupGet(c => c.Settings).Returns(new MongoClientSettings { WriteConcern = WriteConcern.W1 });
        return client;
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var client = BuildAcknowledgedClient();
        Assert.Throws<ArgumentNullException>(() =>
            new MongoDbProcessManagerFinder(
                client.Object,
                new MongoDbPersistenceOptions { DatabaseName = "test" },
                logger: null!));
    }

    [Fact]
    public void Constructor_UnacknowledgedWriteConcern_ThrowsInvalidOperationException()
    {
        var client = new Mock<IMongoClient>();
        client.SetupGet(c => c.Settings).Returns(new MongoClientSettings { WriteConcern = WriteConcern.Unacknowledged });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new MongoDbProcessManagerFinder(
                client.Object,
                new MongoDbPersistenceOptions { DatabaseName = "test" },
                Microsoft.Extensions.Logging.Abstractions.NullLogger<MongoDbProcessManagerFinder>.Instance));

        Assert.Contains("WriteConcern", ex.Message);
        Assert.Contains("acknowledged", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_W1WriteConcern_Succeeds()
    {
        var client = new Mock<IMongoClient>();
        client.SetupGet(c => c.Settings).Returns(new MongoClientSettings { WriteConcern = WriteConcern.W1 });
        var database = new Mock<IMongoDatabase>();
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);

        var finder = new MongoDbProcessManagerFinder(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test" },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MongoDbProcessManagerFinder>.Instance);

        Assert.NotNull(finder);
    }

    [Fact]
    public void Constructor_MajorityWriteConcern_Succeeds()
    {
        var client = new Mock<IMongoClient>();
        client.SetupGet(c => c.Settings).Returns(new MongoClientSettings { WriteConcern = WriteConcern.WMajority });
        var database = new Mock<IMongoDatabase>();
        client.Setup(c => c.GetDatabase("test", null)).Returns(database.Object);

        var finder = new MongoDbProcessManagerFinder(
            client.Object,
            new MongoDbPersistenceOptions { DatabaseName = "test" },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MongoDbProcessManagerFinder>.Instance);

        Assert.NotNull(finder);
    }
}
