using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson.Serialization;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Helpers;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.EndToEndTests;

// Regression pin for the fresh-copy contract on IProcessManagerFinder.FindDataAsync<T>.
//
// MongoDbProcessManagerFinder complies via BSON deserialization: each FindDataAsync
// call executes a fresh MongoDB query and deserializes a new CLR object from the
// BSON response. The two returned Data references must therefore be distinct objects,
// ensuring that a handler mutating Data in one call cannot affect the next call.
[Collection(nameof(PersistenceCollection))]
public class MongoDbProcessManagerFinderFreshDataTests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    static MongoDbProcessManagerFinderFreshDataTests()
    {
        // AutoMap pins the Standard Guid serializer for CorrelationId before the
        // first BSON read/write. IsClassMapRegistered guards against duplicate
        // registration when xUnit loads the assembly in parallel.
        if (!BsonClassMap.IsClassMapRegistered(typeof(FreshCopySagaData)))
        {
            BsonClassMap.RegisterClassMap<FreshCopySagaData>(cm =>
            {
                cm.AutoMap();
                cm.SetIsRootClass(true);
            });
        }
    }

    public class FreshCopySagaData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public string Name { get; set; } = "";
    }

    private MongoDbProcessManagerFinder CreateFinder(string prefix)
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = _fixture.GetUniqueDatabaseName(prefix),
        };
        var client = MongoClientFactory.Create(options);
        return new MongoDbProcessManagerFinder(client, options, NullLogger<MongoDbProcessManagerFinder>.Instance);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task FindDataAsync_ReturnsFreshDataReferencePerCall()
    {
        // Arrange
        var finder = CreateFinder("pmf_freshcopy");
        var corrId = Guid.NewGuid();

        await finder.InsertDataAsync(new FreshCopySagaData { CorrelationId = corrId, Name = "Original" });

        var mapper = new TestProcessManagerPropertyMapper();
        mapper.ConfigureMapping<FreshCopySagaData, Message>(
            saga => saga.CorrelationId,
            msg => msg.CorrelationId);

        var msg = new Message(corrId);

        // Act
        var first = await finder.FindDataAsync<FreshCopySagaData>(mapper, msg);
        var second = await finder.FindDataAsync<FreshCopySagaData>(mapper, msg);

        // Assert
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.False(ReferenceEquals(first!.Data, second!.Data),
            "FindDataAsync must return a fresh Data instance per call so handler mutation can't leak across retries.");
    }
}
