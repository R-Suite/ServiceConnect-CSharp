using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistence.MongoDb;

public static class MongoDbPersistenceExtensions
{
    public static ServiceConnectBuilder UseMongoDbPersistence(
        this ServiceConnectBuilder builder,
        Action<MongoDbPersistenceOptions> configure)
    {
        var options = new MongoDbPersistenceOptions();
        configure(options);

        builder.AdditionalRegistrations.Add(services =>
        {
            services.TryAddSingleton(options);
            services.TryAddSingleton(_ => MongoClientFactory.Create(options));
            services.TryAddSingleton<IAggregatorPersistor, MongoDbAggregatorPersistor>();
            services.TryAddSingleton<IProcessManagerFinder, MongoDbProcessManagerFinder>();
        });

        return builder;
    }
}
