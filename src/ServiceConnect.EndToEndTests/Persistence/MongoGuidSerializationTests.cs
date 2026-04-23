using MongoDB.Bson;
using MongoDB.Driver;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(PersistenceCollection))]
public class MongoGuidSerializationTests
{
    private readonly PersistenceFixture _fixture;

    public MongoGuidSerializationTests(PersistenceFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task InsertAndFilterByGuid_UsesStandardBinarySubtype()
    {
        // Regression guard for MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered /
        // ModuleInit.Initialize. Under V2 legacy mode the stored Guid uses CSharpLegacy
        // (subtype 3) while filter literals built via `x => x.Id == theGuid` serialize as
        // Standard (subtype 4), so the filter silently matches zero documents. After V3
        // setup, both sides use subtype 4 and the round-trip works.

        var dbName = _fixture.GetUniqueDatabaseName("guidserde");
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = dbName,
        };
        var client = MongoClientFactory.Create(options);
        var typedCollection = client.GetDatabase(dbName).GetCollection<TimeoutData>("Timeouts");

        var id = Guid.NewGuid();
        var processManagerId = Guid.NewGuid();
        await typedCollection.InsertOneAsync(new TimeoutData
        {
            Id = id,
            ProcessManagerId = processManagerId,
            Destination = "guid-test",
            Time = DateTimeOffset.UtcNow.AddMinutes(-1),
            Locked = false,
            LockedBy = Guid.Empty,
            Headers = new Dictionary<string, object> { ["k"] = "v" },
        });

        // 1) Filter round-trip: the stored Guid must match a filter built from the same Guid.
        var hits = await typedCollection.Find(x => x.Id == id).ToListAsync();
        Assert.Single(hits);
        Assert.Equal(id, hits[0].Id);
        Assert.Equal(processManagerId, hits[0].ProcessManagerId);

        // 2) Subtype inspection: the stored Guid must use subtype 4 (UUID Standard),
        //    not subtype 3 (CSharpLegacy) — silent V2 fallback stores subtype 3 and
        //    typed filters would then silently miss.
        var rawCollection = client.GetDatabase(dbName).GetCollection<BsonDocument>("Timeouts");
        var raw = await rawCollection.Find(Builders<BsonDocument>.Filter.Empty).FirstAsync();
        var idField = raw["_id"].AsBsonBinaryData;
        Assert.Equal(BsonBinarySubType.UuidStandard, idField.SubType);

        var processManagerIdField = raw["ProcessManagerId"].AsBsonBinaryData;
        Assert.Equal(BsonBinarySubType.UuidStandard, processManagerIdField.SubType);
    }
}
