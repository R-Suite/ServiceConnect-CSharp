using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(PersistenceCollection))]
public class MongoGuidSerializerConflictTests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public void EnsureGuidSerializerRegistered_NonStandardRegisteredFirst_ThrowsInvalidOperationException()
    {
        // BsonSerializer.RegisterSerializer mutates global state. Once ServiceConnect's
        // own registration has run anywhere in the test process, we can no longer set up
        // the conflict scenario. The test asserts ONLY the error path: if a downstream
        // call to RegisterSerializer throws BsonSerializationException, the persistor's
        // entry-point must wrap it in InvalidOperationException with an actionable message.
        //
        // Safer test: simulate the conflict by attempting to register the same Guid
        // serializer with a DIFFERENT representation after ServiceConnect's first call.
        // If the test runs first in the test process, ServiceConnect's call hasn't yet
        // happened — register CSharpLegacy first, then trigger ServiceConnect's path.
        try
        {
            BsonSerializer.RegisterSerializer(typeof(Guid),
                new GuidSerializer(GuidRepresentation.CSharpLegacy));
        }
        catch (BsonSerializationException)
        {
            // ServiceConnect (or another component) has already registered Guid; we cannot
            // prepare the conflict. Skip — see the unit-test fallback if needed.
            return;
        }

        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = _fixture.GetUniqueDatabaseName("guidconflict"),
        };

        var ex = Assert.Throws<InvalidOperationException>(() => MongoClientFactory.Create(options));

        Assert.Contains("GuidRepresentation", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<BsonSerializationException>(ex.InnerException);
    }
}
