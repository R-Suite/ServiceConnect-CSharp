using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistence.InMemory;

public static class InMemoryPersistenceExtensions
{
    public static ServiceConnectBuilder UseInMemoryPersistence(this ServiceConnectBuilder builder)
    {
        builder.AdditionalRegistrations.Add(services =>
        {
            services.TryAddSingleton<ICacheProvider, CacheProvider>();
            services.TryAddSingleton<IAggregatorPersistor>(_ =>
                new InMemoryAggregatorPersistor("", "", ""));
            services.TryAddSingleton<IProcessManagerFinder>(_ =>
                new InMemoryProcessManagerFinder("", ""));
        });
        return builder;
    }
}
