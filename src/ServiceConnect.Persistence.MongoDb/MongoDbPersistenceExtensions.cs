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
    // which is Standard once we register it below.
    //
    // The success flag is set only after every step (mode toggle, verification, serializer
    // registration) completes without throwing. A broken init therefore throws on every call
    // — no silent short-circuit into a misconfigured MongoClient — until the underlying
    // configuration problem is fixed. The first-time setup is serialised via the lock so
    // concurrent callers don't race on the global BSON mutations.
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

            Exception? modeToggleError = null;
            try
            {
#pragma warning disable CS0618 // GuidRepresentationMode is obsolete but v3 is mandatory for Standard-representation semantics in MongoDB.Driver 2.x.
                BsonDefaults.GuidRepresentationMode = GuidRepresentationMode.V3;
#pragma warning restore CS0618
            }
            catch (InvalidOperationException ex)
            {
                // Driver forbids toggling once serialization started. Capture the error so we can
                // report it as InnerException if the subsequent verification fails.
                modeToggleError = ex;
            }

            // Toggle, then verify. If the effective mode is still V2 — because another component
            // in the process set it first or serialization has already begun — our Guid filter
            // literals will use subtype 4 (Standard) while stored Guid members fall back to
            // CSharpLegacy subtype 3 and every filter silently misses. Fail loudly so the
            // misconfiguration is visible at startup rather than as a silent data-access outage.
#pragma warning disable CS0618
            if (BsonDefaults.GuidRepresentationMode != GuidRepresentationMode.V3)
            {
                throw new InvalidOperationException(
                    "ServiceConnect MongoDB persistence requires BsonDefaults.GuidRepresentationMode = V3 " +
                    "for Guid filter literals to match stored values. The current mode is " +
                    $"{BsonDefaults.GuidRepresentationMode}. Another component in the process has set " +
                    "it to a different value before ServiceConnect initialised, or serialization has " +
                    "already begun and the mode is now frozen. Configure your driver initialisation to " +
                    "set V3 before any BSON serialization begins.",
                    modeToggleError);
            }
#pragma warning restore CS0618

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

            // Only commit the success flag after every step above has completed without
            // throwing. If the verification above throws, the flag stays 0 and the next
            // caller retries the whole setup instead of short-circuiting on broken state.
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
            services.TryAddSingleton<MongoDbTimeoutStore>();
            services.TryAddSingleton<IProcessManagerFinder>(sp =>
                sp.GetRequiredService<MongoDbProcessManagerFinder>());
            services.TryAddSingleton<ITimeoutStore>(sp =>
                sp.GetRequiredService<MongoDbTimeoutStore>());
        });

        return builder;
    }
}
