using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceConnect;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class MongoDbPersistenceExtensionsIdempotencyTests
{
    [Fact]
    public void UseMongoDbPersistence_CalledTwice_RegistersOneIndexInitializer()
    {
        var builder = new ServiceConnectBuilder();
        builder.UseMongoDbPersistence(opt => opt.ConnectionString = "mongodb://localhost:27017");
        builder.UseMongoDbPersistence(opt => opt.ConnectionString = "mongodb://localhost:27017");

        var services = new ServiceCollection();
        foreach (var registration in builder.AdditionalRegistrations)
        {
            registration(services);
        }

        var initializerCount = services.Count(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(MongoDbProcessManagerIndexInitializer));

        Assert.Equal(1, initializerCount);
    }
}
