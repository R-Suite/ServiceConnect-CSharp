using Microsoft.Extensions.DependencyInjection;
using ServiceConnect;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests.Builder;

[Collection("Mongo Bson serial")]
public class PersistenceRegistrationTests
{
    [Fact]
    public void UseInMemoryPersistence_RegistersDistinctFinderAndTimeoutStore()
    {
        var builder = new ServiceConnectBuilder();

        builder.UseInMemoryPersistence();

        Assert.Single(builder.AdditionalRegistrations);
        var services = new ServiceCollection();
        builder.AdditionalRegistrations[0](services);

        Assert.Contains(services, sd => sd.ServiceType == typeof(InMemoryProcessManagerFinder));
        Assert.Contains(services, sd => sd.ServiceType == typeof(InMemoryTimeoutStore));
        Assert.Contains(services, sd => sd.ServiceType == typeof(IProcessManagerFinder));
        Assert.Contains(services, sd => sd.ServiceType == typeof(ITimeoutStore));
        Assert.DoesNotContain(services, sd =>
            sd.ServiceType == typeof(ITimeoutStore) &&
            sd.ImplementationType == typeof(InMemoryProcessManagerFinder));
    }

    [Fact]
    public void UseMongoDbPersistence_RegistersDistinctFinderAndTimeoutStore()
    {
        var builder = new ServiceConnectBuilder();

        builder.UseMongoDbPersistence(_ => { });

        Assert.Single(builder.AdditionalRegistrations);
        var services = new ServiceCollection();
        builder.AdditionalRegistrations[0](services);

        Assert.Contains(services, sd => sd.ServiceType == typeof(MongoDbProcessManagerFinder));
        Assert.Contains(services, sd => sd.ServiceType == typeof(MongoDbTimeoutStore));
        Assert.Contains(services, sd => sd.ServiceType == typeof(IProcessManagerFinder));
        Assert.Contains(services, sd => sd.ServiceType == typeof(ITimeoutStore));
        Assert.DoesNotContain(services, sd =>
            sd.ServiceType == typeof(ITimeoutStore) &&
            sd.ImplementationType == typeof(MongoDbProcessManagerFinder));
    }
}
