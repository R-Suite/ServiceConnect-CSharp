using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class HealthCheckRegistrationCachingTests
{
    [Fact]
    public void AddServiceConnectBus_TwoProbesShareTheSameInstance()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IBus>(b => b.IsConsuming == true));
        services.AddHealthChecks().AddServiceConnectBus("test");

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var registration = options.Registrations.Single(r => r.Name == "test");

        var first = registration.Factory(sp);
        var second = registration.Factory(sp);

        Assert.Same(first, second);
    }

    [Fact]
    public void AddServiceConnectConsumer_TwoProbesShareTheSameInstance()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IConsumer>(c => c.IsConnected == true));
        services.AddHealthChecks().AddServiceConnectConsumer("test");

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var registration = options.Registrations.Single(r => r.Name == "test");

        var first = registration.Factory(sp);
        var second = registration.Factory(sp);

        Assert.Same(first, second);
    }

    [Fact]
    public void AddServiceConnectProducer_TwoProbesShareTheSameInstance()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<IProducer>(p => p.IsHealthy == true));
        services.AddHealthChecks().AddServiceConnectProducer("test");

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var registration = options.Registrations.Single(r => r.Name == "test");

        var first = registration.Factory(sp);
        var second = registration.Factory(sp);

        Assert.Same(first, second);
    }
}
