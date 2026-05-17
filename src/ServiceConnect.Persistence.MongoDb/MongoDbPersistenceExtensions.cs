using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
    // MongoDB.Driver 3.x always operates in the V3 GuidRepresentation regime — there is no
    // mode toggle, and every Guid member honours the serializer attached to it. We still
    // register a Standard-representation Guid serializer (subtype 4 / UUID per RFC) so that
    // stored Guid properties round-trip with the same subtype the filter literals are built
    // against in IMongoCollection<T> queries. Without an explicit registration the driver
    // defaults to Standard for net8+ anyway, but pinning it here guards against another
    // component in the process registering a different serializer first.
    //
    // The success flag is set only after registration completes without throwing. A broken
    // init therefore throws on every call — no silent short-circuit into a misconfigured
    // MongoClient — until the underlying configuration is valid. First-time setup is
    // serialised via the lock so concurrent callers don't race on the global BSON mutations.
    private static int _guidSerializerRegistered;
#if NET9_0_OR_GREATER
    private static readonly System.Threading.Lock GuidSerializerInitLock = new();
#else
    private static readonly object GuidSerializerInitLock = new();
#endif

    internal static void EnsureGuidSerializerRegistered()
    {
        // Fast path: lock-free observation for hot-path callers on already-initialised
        // processes. A volatile read is enough to see the write that committed the flag
        // under the lock — a matching release/acquire pair — without acquiring the lock.
        if (Volatile.Read(ref _guidSerializerRegistered) != 0)
        {
            return;
        }

        lock (GuidSerializerInitLock)
        {
            // Re-check under the lock: if a concurrent first caller won the race and
            // completed setup while we were waiting, there is nothing left to do.
            if (_guidSerializerRegistered != 0)
            {
                return;
            }

            try
            {
                BsonSerializer.RegisterSerializer(typeof(Guid), new GuidSerializer(GuidRepresentation.Standard));
            }
            catch (BsonSerializationException ex)
            {
                // Another component already registered a Guid serializer. Accept it only if it is
                // already Standard — reusing it is safe because our filter literals will match.
                // Any other representation causes silent query misses (filter literals use
                // subtype 4 while stored Guids use a different subtype), so we fail loudly.
                var registered = BsonSerializer.LookupSerializer<Guid>();
                if (!IsCompatibleGuidSerializer(registered))
                {
                    throw new InvalidOperationException(
                        "ServiceConnect MongoDB persistence requires GuidRepresentation.Standard but another " +
                        "component has already registered a different Guid serializer. Configure your driver " +
                        "initialisation to either skip Guid serializer registration or register " +
                        "GuidRepresentation.Standard before any other component does so.",
                        ex);
                }
                // Compatible Standard already registered (by another component or our own
                // duplicate call); proceed.
            }

            // Post-register verification: confirm the registry actually holds a Standard-Guid
            // serializer before flipping the success flag. A RegisterSerializer call that
            // returns success doesn't strictly guarantee our serializer is the one the lookup
            // path will return — a concurrent component could race us between our register
            // call and the first query. Round-trip the lookup once so we fail loudly here
            // rather than producing silent query misses against filter literals later.
            var verified = BsonSerializer.LookupSerializer<Guid>();
            if (!IsCompatibleGuidSerializer(verified))
            {
                throw new InvalidOperationException(
                    "ServiceConnect MongoDB persistence registered GuidRepresentation.Standard but a " +
                    "different Guid serializer is now active (possibly registered concurrently by " +
                    "another component). Configure your driver initialisation to install " +
                    "GuidRepresentation.Standard exclusively, before any other component registers a " +
                    "Guid serializer.");
            }

            // Only commit the success flag after registration AND verification have completed
            // without throwing. If verification fails, the flag stays 0 and the next caller
            // retries the whole setup instead of short-circuiting on broken state.
            Volatile.Write(ref _guidSerializerRegistered, 1);
        }
    }

    /// <summary>
    /// Returns true when an existing Guid serializer registration is compatible with
    /// ServiceConnect's requirement (GuidRepresentation.Standard). False otherwise —
    /// indicating a mismatch that should be reported loudly.
    /// </summary>
    internal static bool IsCompatibleGuidSerializer(IBsonSerializer<Guid>? registered)
    {
        return registered is GuidSerializer guidSerializer
            && guidSerializer.GuidRepresentation == GuidRepresentation.Standard;
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
            // Pre-create per-saga unique CorrelationId indexes at startup. Closes the
            // cross-process race window where two cold-started processes could insert
            // duplicate saga rows before either one called the lazy index-creation path.
            // INSERT at position 0 so the initializer runs BEFORE BusHostedService — IHost
            // starts hosted services in registration order, and a default AddHostedService
            // call appends, which would put indexing AFTER consuming starts and leave the
            // race window open during cold-start.
            //
            // Idempotent: two feature modules each calling UseMongoDbPersistence within one
            // AddServiceConnect must not produce two initializer instances racing the same
            // index-creation work. TryAddEnumerable would append rather than position-0
            // insert, defeating the pre-BusHostedService ordering — guard with an explicit
            // type-check instead.
            if (!services.Any(d => d.ImplementationType == typeof(MongoDbProcessManagerIndexInitializer)))
            {
                services.Insert(0, ServiceDescriptor.Singleton<IHostedService, MongoDbProcessManagerIndexInitializer>());
            }
            services.TryAddSingleton<MongoDbTimeoutStore>();
            services.TryAddSingleton<IProcessManagerFinder>(sp =>
                sp.GetRequiredService<MongoDbProcessManagerFinder>());
            services.TryAddSingleton<ITimeoutStore>(sp =>
                sp.GetRequiredService<MongoDbTimeoutStore>());
        });

        return builder;
    }
}
