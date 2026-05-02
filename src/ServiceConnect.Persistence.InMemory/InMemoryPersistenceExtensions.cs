using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    public static ServiceConnectBuilder UseInMemoryPersistence(this ServiceConnectBuilder builder)
    {
        builder.AddRegistration(services =>
        {
            services.TryAddSingleton<ProcessManagerPredicateCache>();
            services.TryAddSingleton<InMemoryPersistenceOptions>();
            services.TryAddSingleton<InMemoryPersistenceState>(sp =>
                new InMemoryPersistenceState(sp.GetRequiredService<TimeProvider>()));
            services.TryAddSingleton<ICacheProvider>(sp =>
                sp.GetRequiredService<InMemoryPersistenceState>().Provider);
            services.TryAddSingleton<IKeyValueStore>(sp =>
                (IKeyValueStore)sp.GetRequiredService<InMemoryPersistenceState>().Provider);
            services.TryAddSingleton<IAggregatorPersistor>(sp =>
                new InMemoryAggregatorPersistor("", "", "", sp.GetRequiredService<TimeProvider>()));
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
