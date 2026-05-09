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
/// MongoDB.Driver 2.x defaults <c>GuidRepresentationMode</c> to <c>V2</c>, in which the
/// per-serializer Guid representation is ignored on writes and the legacy
/// <c>MongoClientSettings.GuidRepresentation</c> (<c>CSharpLegacy</c>, subtype 3) is used
/// instead. Filter literals built via <c>x =&gt; x.Data.CorrelationId</c> use ServiceConnect's
/// registered Standard serializer (subtype 4), so under V2 they silently miss every
/// stored document. Flipping to <c>V3</c> here, before any BSON serialization begins,
/// makes every Guid member honour the registered serializer.
/// </para>
/// <para>
/// Registering the <c>GuidSerializer(Standard)</c> here also stops the in-memory persistors'
/// <c>DeepClone</c> path (which lazily builds a <c>BsonClassMap&lt;Message&gt;</c> on first use)
/// from caching a class map with <c>GuidRepresentation.Unspecified</c>. Once a class map is
/// cached, later changes to the registry don't update it, so tests must agree on the
/// registry state before any class map is built — and the only safe place to do that
/// is module init.
/// </para>
/// <para>
/// <c>GuidSerializerRegistrationTests</c> resets the static <c>_guidSerializerRegistered</c>
/// flag inside <see cref="ServiceConnect.Persistence.MongoDb.MongoDbPersistenceExtensions"/>
/// via reflection to exercise the once-only guard. Without this module initializer, that
/// reset would expose other tests (running in parallel collections) to a brief window where
/// the BSON registry was being re-mutated, racing with their own first-use class-map build.
/// With the registry pre-populated here, the re-registration call on the second pass is a
/// compatible no-op (the catch block in <c>EnsureGuidSerializerRegistered</c> succeeds because
/// the already-registered serializer is the same Standard <c>GuidSerializer</c>).
/// </para>
/// </remarks>
internal static class ModuleInit
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Exception? modeToggleError = null;
        try
        {
#pragma warning disable CS0618 // GuidRepresentationMode is obsolete but V3 is mandatory for Standard-representation semantics in MongoDB.Driver 2.x.
            BsonDefaults.GuidRepresentationMode = GuidRepresentationMode.V3;
#pragma warning restore CS0618
        }
        catch (InvalidOperationException ex)
        {
            // Mode has already been frozen by the driver (serialization started). The
            // verification below will fail loudly if the frozen mode is anything other
            // than V3 — capturing this exception keeps it as InnerException for diagnosis.
            modeToggleError = ex;
        }

#pragma warning disable CS0618
        if (BsonDefaults.GuidRepresentationMode != GuidRepresentationMode.V3)
        {
            throw new InvalidOperationException(
                "ServiceConnect.UnitTests requires BsonDefaults.GuidRepresentationMode = V3 for Guid " +
                "filter literals to match stored values. The current mode is " +
                $"{BsonDefaults.GuidRepresentationMode}. Another component loaded before this module " +
                "initializer has set it to a different value, or BSON serialization has already begun " +
                "and the driver has frozen the mode. Ensure no static constructor touches the BSON " +
                "driver before this module initializer runs.",
                modeToggleError);
        }
#pragma warning restore CS0618

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
