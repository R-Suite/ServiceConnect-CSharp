using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ServiceConnect.Filters.MessageDeduplication;
using ServiceConnect.Filters.MessageDeduplication.Filters;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using Xunit;

namespace ServiceConnect.Filters.MessageDeduplication.Tests;

public class AddMessageDeduplicationFilterTests
{
    [Fact]
    public void InMemoryType_ResolvesInMemoryPersistor()
    {
        var services = new ServiceCollection();
        services.AddMessageDeduplicationFilter(cfg =>
        {
            cfg.PersistorType = PersistorType.InMemory;
        });

        var provider = services.BuildServiceProvider();
        var persistor = provider.GetRequiredService<IMessageDeduplicationPersistor>();

        Assert.IsType<MessageDeduplicationPersistorInMemory>(persistor);
    }

    [Fact]
    public void RegistersOptions_BoundToUserConfiguration()
    {
        var services = new ServiceCollection();
        services.AddMessageDeduplicationFilter(cfg =>
        {
            cfg.PersistorType = PersistorType.InMemory;
            cfg.MsgExpiryHours = 48;
            cfg.MsgCleanupIntervalMinutes = 5;
        });

        var provider = services.BuildServiceProvider();
        var opts = provider.GetRequiredService<IOptions<DeduplicationFilterSettings>>().Value;

        Assert.Equal(48, opts.MsgExpiryHours);
        Assert.Equal(5, opts.MsgCleanupIntervalMinutes);
    }

    [Fact]
    public void RegistersBothFilters()
    {
        var services = new ServiceCollection();
        services.AddMessageDeduplicationFilter(cfg => { cfg.PersistorType = PersistorType.InMemory; });

        var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<OutgoingDeduplicationFilter>());
        Assert.NotNull(provider.GetRequiredService<IncomingDeduplicationFilter>());
    }

    [Fact]
    public void RegistersCleanupHostedService()
    {
        var services = new ServiceCollection();
        services.AddMessageDeduplicationFilter(cfg => { cfg.PersistorType = PersistorType.InMemory; });

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IHostedService) &&
            d.ImplementationType == typeof(DeduplicationCleanupHostedService));
    }

    [Fact]
    public void NullConfigure_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<System.ArgumentNullException>(() =>
            services.AddMessageDeduplicationFilter(null!));
    }
}
