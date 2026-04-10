using Microsoft.Extensions.DependencyInjection;
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
        // Register in DI - extend builder to support additional registrations
        return builder;
    }
}
