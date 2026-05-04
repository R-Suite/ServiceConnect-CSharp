using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Adds the in-memory persistence services used by ServiceConnect.
/// </summary>
public static class InMemoryPersistenceExtensions
{
    /// <summary>
    /// Registers the in-memory persistence implementation with the builder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Intended for development and tests.</b> All state is held in-process in
    /// <see cref="InMemoryPersistenceState"/> and is not durable across restarts. This is the
    /// right choice for unit and integration tests that need a real persistor without provisioning
    /// infrastructure, and for local development.
    /// </para>
    /// <para>
    /// <b>Do not use in production.</b> A process restart loses every in-flight process-manager
    /// instance, every pending aggregator group, and every scheduled timeout. Use a durable
    /// persistor (e.g. <c>UseMongoDbPersistence</c>) for production deployments.
    /// </para>
    /// <para>
    /// At bus build time this method emits a <see cref="LogLevel.Warning"/>-level log under
    /// the <c>ServiceConnect.Persistence.InMemory</c> category to surface the test/dev scope at
    /// runtime. The warning fires once per bus instance. To silence in test runs, raise the
    /// category's minimum level to <see cref="LogLevel.Error"/> via standard
    /// <c>Microsoft.Extensions.Logging</c> filter configuration.
    /// </para>
    /// </remarks>
    /// <param name="builder">The ServiceConnect builder.</param>
    /// <param name="configure">
    /// Optional delegate to customise <see cref="InMemoryPersistenceOptions"/> before registration.
    /// When omitted the defaults (e.g. a five-minute lock-lease duration) are used.
    /// </param>
    public static ServiceConnectBuilder UseInMemoryPersistence(
        this ServiceConnectBuilder builder,
        Action<InMemoryPersistenceOptions>? configure = null)
    {
        // Build the options instance once at extension-call time so the configure
        // delegate's customisations are captured before AddRegistration's callback
        // is invoked (the callback may be called more than once; options must not change).
        var options = new InMemoryPersistenceOptions();
        configure?.Invoke(options);

        builder.AddRegistration(services =>
        {
            services.TryAddSingleton<ProcessManagerPredicateCache>();
            services.TryAddSingleton(options);
            services.TryAddSingleton<InMemoryPersistenceState>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<InMemoryPersistenceState>>();
                InMemoryPersistenceLog.InMemoryPersistenceRegistered(logger);
                return new InMemoryPersistenceState(sp.GetRequiredService<TimeProvider>());
            });
            services.TryAddSingleton<ICacheProvider>(sp =>
                sp.GetRequiredService<InMemoryPersistenceState>().Provider);
            services.TryAddSingleton<IKeyValueStore>(sp =>
                (IKeyValueStore)sp.GetRequiredService<InMemoryPersistenceState>().Provider);
            services.TryAddSingleton<IAggregatorPersistor>(sp =>
                new InMemoryAggregatorPersistor(sp.GetRequiredService<TimeProvider>()));
            services.TryAddSingleton<InMemoryProcessManagerFinder>(sp =>
                new InMemoryProcessManagerFinder(
                    sp.GetRequiredService<ProcessManagerPredicateCache>(),
                    sp.GetRequiredService<InMemoryPersistenceState>()));
            services.TryAddSingleton<InMemoryTimeoutStore>(sp =>
                new InMemoryTimeoutStore(
                    sp.GetRequiredService<InMemoryPersistenceOptions>(),
                    sp.GetRequiredService<InMemoryPersistenceState>(),
                    sp.GetRequiredService<TimeProvider>()));
            services.TryAddSingleton<IProcessManagerFinder>(sp =>
                sp.GetRequiredService<InMemoryProcessManagerFinder>());
            services.TryAddSingleton<ITimeoutStore>(sp =>
                sp.GetRequiredService<InMemoryTimeoutStore>());
        });
        return builder;
    }
}
