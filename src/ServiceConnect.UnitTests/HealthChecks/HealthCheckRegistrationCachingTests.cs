using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

/// <summary>
/// Post-M6: the registration factory no longer caches a closure-captured check instance
/// (the closure outlived the IServiceProvider, so a rebuilt provider would probe a stale
/// check wrapping a disposed dependency). Each probe builds a fresh wrapper around the
/// IBus/IConsumer/IProducer resolved from the supplied IServiceProvider — which itself
/// caches the singleton, so the underlying transport object is shared across probes
/// against the same provider.
/// </summary>
public class HealthCheckRegistrationCachingTests
{
    [Fact]
    public void AddServiceConnectBus_TwoProbes_ReturnFreshWrappersOverSameBus()
    {
        var bus = Mock.Of<IBus>(b => b.IsConsuming == true);
        var services = new ServiceCollection();
        services.AddSingleton(bus);
        services.AddHealthChecks().AddServiceConnectBus("test");

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var registration = options.Registrations.Single(r => r.Name == "test");

        var first = registration.Factory(sp);
        var second = registration.Factory(sp);

        // Fresh wrapper per probe — the cache is the IServiceProvider, not us.
        Assert.NotSame(first, second);
        Assert.IsType<BusConsumingHealthCheck>(first);
        Assert.IsType<BusConsumingHealthCheck>(second);
    }

    [Fact]
    public void AddServiceConnectConsumer_TwoProbes_ReturnFreshWrappersOverSameConsumer()
    {
        var consumer = Mock.Of<IConsumer>(c => c.IsConnected == true);
        var services = new ServiceCollection();
        services.AddSingleton(consumer);
        services.AddHealthChecks().AddServiceConnectConsumer("test");

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var registration = options.Registrations.Single(r => r.Name == "test");

        var first = registration.Factory(sp);
        var second = registration.Factory(sp);

        Assert.NotSame(first, second);
        Assert.IsType<ConsumerConnectionHealthCheck>(first);
        Assert.IsType<ConsumerConnectionHealthCheck>(second);
    }

    [Fact]
    public void AddServiceConnectProducer_TwoProbes_ReturnFreshWrappersOverSameProducer()
    {
        var producer = Mock.Of<IProducer>(p =>
            p.GetHealthSnapshot() == new ProducerHealthSnapshot(true, true));
        var services = new ServiceCollection();
        services.AddSingleton(producer);
        services.AddHealthChecks().AddServiceConnectProducer("test");

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var registration = options.Registrations.Single(r => r.Name == "test");

        var first = registration.Factory(sp);
        var second = registration.Factory(sp);

        Assert.NotSame(first, second);
        Assert.IsType<ProducerConnectionHealthCheck>(first);
        Assert.IsType<ProducerConnectionHealthCheck>(second);
    }
}
