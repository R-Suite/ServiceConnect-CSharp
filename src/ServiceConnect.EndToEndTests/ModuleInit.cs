using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace ServiceConnect.EndToEndTests;

internal static class ModuleInit
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // MongoDB.Driver 2.x defaults GuidRepresentationMode to V2, which ignores the
        // per-serializer Guid representation on writes and falls back to the legacy
        // MongoClientSettings.GuidRepresentation (CSharpLegacy = subtype 3). Filter literals
        // built via x => x.Data.CorrelationId use our registered Standard serializer (subtype 4)
        // and never match stored documents. Flipping to V3 makes every Guid member honour the
        // serializer attached to it.
        Exception? modeToggleError = null;
        try
        {
#pragma warning disable CS0618 // GuidRepresentationMode is obsolete but v3 is mandatory for Standard-representation semantics in MongoDB.Driver 2.x.
            BsonDefaults.GuidRepresentationMode = GuidRepresentationMode.V3;
#pragma warning restore CS0618
        }
        catch (InvalidOperationException ex)
        {
            // Mode has already been frozen by the driver (serialization already started).
            modeToggleError = ex;
        }

        // Verify the toggle took effect. If another component has frozen the mode to V2
        // before this module initializer ran, the whole test suite would see silent
        // zero-match Guid filters — fail loudly instead.
#pragma warning disable CS0618
        if (BsonDefaults.GuidRepresentationMode != GuidRepresentationMode.V3)
        {
            throw new InvalidOperationException(
                "Test infrastructure requires BsonDefaults.GuidRepresentationMode = V3; " +
                $"current mode is {BsonDefaults.GuidRepresentationMode}.",
                modeToggleError);
        }
#pragma warning restore CS0618

        // Register Standard (UUID binary subtype 4) Guid serializer *before* any BsonClassMap
        // is auto-built. Once a class map is cached with the default serializer, later
        // registration doesn't update it — so tests that touch a MongoDB-mapped type first
        // would freeze a wrong Guid serializer into the class map.
        try
        {
            BsonSerializer.RegisterSerializer(typeof(Guid), new GuidSerializer(GuidRepresentation.Standard));
        }
        catch
        {
            // Already registered — ignore
        }

        // Configure MongoDB ObjectSerializer to allow all types before any test runs.
        // This must happen before any MongoDB driver usage registers the default ObjectSerializer.
        try
        {
            BsonSerializer.RegisterSerializer(new ObjectSerializer(ObjectSerializer.AllAllowedTypes));
        }
        catch
        {
            // Already registered — ignore
        }
    }
}
