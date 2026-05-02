using Moq;
using MongoDB.Driver;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

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
}
