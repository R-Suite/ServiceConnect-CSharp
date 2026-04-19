using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistence.MongoDb;

/// <summary>
/// Extension methods for registering MongoDB-backed persistence components.
/// </summary>
public static class MongoDbPersistenceExtensions
{
    // MongoDB.Driver 2.x defaults BsonDefaults.GuidRepresentationMode to V2, which ignores
    // the registered GuidSerializer and falls back to MongoClientSettings.GuidRepresentation
    // (CSharpLegacy by default) when writing — so saved Guid properties round-trip as subtype 3
    // (Legacy) while filter literals built via x => x.Data.CorrelationId use the registered
    // Standard serializer (subtype 4), and the filter silently misses documents.
    // Switching to V3 mode makes every Guid member honour the serializer attached to it,
    // which is Standard once we register it below. Both mutations are idempotent-guarded
    // with Interlocked so parallel first-use calls don't clobber settings.
    private static int _guidSerializerRegistered;

    internal static void EnsureGuidSerializerRegistered()
    {
        if (Interlocked.Exchange(ref _guidSerializerRegistered, 1) != 0) return;

        try
        {
#pragma warning disable CS0618 // GuidRepresentationMode is obsolete but v3 is mandatory for Standard-representation semantics in MongoDB.Driver 2.x.
            BsonDefaults.GuidRepresentationMode = GuidRepresentationMode.V3;
#pragma warning restore CS0618
        }
        catch (InvalidOperationException)
        {
            // Mode has already been set (driver forbids toggling once serialization started).
        }

        try
        {
            BsonSerializer.RegisterSerializer(typeof(Guid), new GuidSerializer(GuidRepresentation.Standard));
        }
        catch (BsonSerializationException)
        {
            // Another component registered a different Guid serializer first — respect that,
            // but keep the Standard-representation query literals in our own filters.
        }
    }

    /// <summary>
    /// Registers MongoDB implementations for ServiceConnect persistence services.
    /// </summary>
    /// <param name="builder">The builder to configure.</param>
    /// <param name="configure">Applies MongoDB persistence options.</param>
    /// <returns>The same <see cref="ServiceConnectBuilder"/> instance.</returns>
    public static ServiceConnectBuilder UseMongoDbPersistence(
        this ServiceConnectBuilder builder,
        Action<MongoDbPersistenceOptions> configure)
    {
        var options = new MongoDbPersistenceOptions();
        configure(options);

        EnsureGuidSerializerRegistered();

        builder.AddRegistration(services =>
        {
            services.TryAddSingleton(options);
            // Single IMongoClient singleton — shared across all persistence classes so
            // only one connection pool is created.
            services.TryAddSingleton<IMongoClient>(_ => MongoClientFactory.Create(options));
            services.TryAddSingleton<IAggregatorPersistor, MongoDbAggregatorPersistor>();
            services.TryAddSingleton<MongoDbProcessManagerFinder>();
            services.TryAddSingleton<MongoDbTimeoutStore>();
            services.TryAddSingleton<IProcessManagerFinder>(sp =>
                sp.GetRequiredService<MongoDbProcessManagerFinder>());
            services.TryAddSingleton<ITimeoutStore>(sp =>
                sp.GetRequiredService<MongoDbTimeoutStore>());
        });

        return builder;
    }
}
