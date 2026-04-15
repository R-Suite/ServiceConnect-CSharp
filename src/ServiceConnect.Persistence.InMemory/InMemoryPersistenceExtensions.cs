using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistence.InMemory;

public static class InMemoryPersistenceExtensions
{
    public static ServiceConnectBuilder UseInMemoryPersistence(this ServiceConnectBuilder builder)
    {
        builder.AddRegistration(services =>
        {
            services.TryAddSingleton<ProcessManagerPredicateCache>();
            services.TryAddSingleton<InMemoryPersistenceState>(sp =>
                new InMemoryPersistenceState(sp.GetRequiredService<TimeProvider>()));
            services.TryAddSingleton<ICacheProvider>(sp =>
                sp.GetRequiredService<InMemoryPersistenceState>().Provider);
            services.TryAddSingleton<IKeyValueStore>(sp =>
                sp.GetRequiredService<InMemoryPersistenceState>().Provider);
            services.TryAddSingleton<IAggregatorPersistor>(sp =>
                new InMemoryAggregatorPersistor("", "", "", sp.GetRequiredService<TimeProvider>()));
            services.TryAddSingleton<InMemoryProcessManagerFinder>(sp =>
                new InMemoryProcessManagerFinder(
                    sp.GetRequiredService<ProcessManagerPredicateCache>(),
                    sp.GetRequiredService<InMemoryPersistenceState>(),
                    sp.GetRequiredService<TimeProvider>()));
            services.TryAddSingleton<InMemoryTimeoutStore>(sp =>
                new InMemoryTimeoutStore(
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
