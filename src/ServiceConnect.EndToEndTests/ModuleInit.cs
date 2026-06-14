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
        // MongoDB.Driver 3.x always operates in the V3 GuidRepresentation regime — every
        // Guid member honours the per-serializer representation. We register Standard
        // (UUID binary subtype 4) here *before* any BsonClassMap is auto-built so that
        // tests touching a MongoDB-mapped type first don't freeze a wrong Guid serializer
        // into a cached class map.
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
