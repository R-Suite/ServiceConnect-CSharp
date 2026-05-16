using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Bootstraps the MongoDB BSON globals exactly once at assembly load — before any test
/// class touches a Mongo persistor or the in-memory persistors' BSON-backed DeepClone.
/// </summary>
/// <remarks>
/// <para>
/// MongoDB.Driver 3.x always operates in the V3 GuidRepresentation regime, so every Guid
/// member honours the per-serializer representation. We still register a Standard
/// <c>GuidSerializer</c> here so that stored Guid properties use subtype 4 (UUID per RFC)
/// — matching the serializer the production persistors register and the filter literals
/// built via <c>x =&gt; x.Data.CorrelationId</c> compile against.
/// </para>
/// <para>
/// Registering at module init stops the in-memory persistors' <c>DeepClone</c> path
/// (which lazily builds a <c>BsonClassMap&lt;Message&gt;</c> on first use) from caching a
/// class map with the wrong Guid representation. Once a class map is cached, later
/// changes to the registry don't update it — so tests must agree on the registry state
/// before any class map is built, and the only safe place to do that is module init.
/// </para>
/// <para>
/// <c>GuidSerializerRegistrationTests</c> resets the static <c>_guidSerializerRegistered</c>
/// flag inside <see cref="ServiceConnect.Persistence.MongoDb.MongoDbPersistenceExtensions"/>
/// via reflection to exercise the once-only guard. With the registry pre-populated here,
/// the re-registration call on the second pass is a compatible no-op (the catch block in
/// <c>EnsureGuidSerializerRegistered</c> succeeds because the already-registered serializer
/// is the same Standard <c>GuidSerializer</c>).
/// </para>
/// </remarks>
internal static class ModuleInit
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Register Standard Guid serializer before any BsonClassMap is auto-built.
        // Once a class map is cached with a different serializer, later registration
        // doesn't update it — so any test that touches a Mongo-mapped type or builds
        // a class map via DeepClone first would freeze the wrong serializer into the
        // map. This call wins the registration race against test code by virtue of
        // happening at module init, before any test discovery completes.
        try
        {
            BsonSerializer.RegisterSerializer(typeof(Guid), new GuidSerializer(GuidRepresentation.Standard));
        }
        catch
        {
            // Already registered by another component (e.g., a referenced assembly's own
            // ModuleInitializer). Treat as a no-op — the Mongo persistor's
            // EnsureGuidSerializerRegistered will reject it explicitly later if the
            // existing registration is incompatible.
        }
    }
}
