using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class AddServiceConnectBusTests
{
    private static HealthCheckRegistration GetSingleRegistration(IServiceCollection services)
    {
        services.AddSingleton(new Mock<IBus>().Object);
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        return Assert.Single(options.Registrations);
    }

    [Fact]
    public void AddServiceConnectBus_DefaultName_IsServiceConnectBus()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectBus();

        var registration = GetSingleRegistration(services);
        Assert.Equal("serviceconnect-bus", registration.Name);
    }

    [Fact]
    public void AddServiceConnectBus_CustomName_Propagates()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectBus(name: "custom");

        var registration = GetSingleRegistration(services);
        Assert.Equal("custom", registration.Name);
    }

    [Fact]
    public void AddServiceConnectBus_TagsAndTimeoutAndFailureStatus_Propagate()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectBus(
            tags: ["live"],
            timeout: TimeSpan.FromSeconds(2),
            failureStatus: HealthStatus.Degraded);

        var registration = GetSingleRegistration(services);
        Assert.Contains("live", registration.Tags);
        Assert.Equal(TimeSpan.FromSeconds(2), registration.Timeout);
        Assert.Equal(HealthStatus.Degraded, registration.FailureStatus);
    }

    [Fact]
    public void AddServiceConnectBus_RegistrationFactory_ResolvesBusConsumingHealthCheck()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IBus>().Object);
        services.AddHealthChecks().AddServiceConnectBus();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var registration = Assert.Single(options.Registrations);

        var instance = registration.Factory(provider);
        Assert.IsType<BusConsumingHealthCheck>(instance);
    }
}
