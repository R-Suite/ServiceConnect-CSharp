using System.Runtime.CompilerServices;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace ServiceConnect.EndToEndTests;

internal static class ModuleInit
{
    [ModuleInitializer]
    internal static void Initialize()
    {
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
