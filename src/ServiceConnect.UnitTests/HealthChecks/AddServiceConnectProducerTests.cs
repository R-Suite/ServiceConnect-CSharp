using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class AddServiceConnectProducerTests
{
    private static HealthCheckRegistration GetSingleRegistration(IServiceCollection services)
    {
        services.AddSingleton(new Mock<IProducer>().Object);
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        return Assert.Single(options.Registrations);
    }

    [Fact]
    public void AddServiceConnectProducer_DefaultName_IsServiceConnectProducer()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectProducer();

        var registration = GetSingleRegistration(services);
        Assert.Equal("serviceconnect-producer", registration.Name);
    }

    [Fact]
    public void AddServiceConnectProducer_CustomName_Propagates()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectProducer(name: "custom");

        var registration = GetSingleRegistration(services);
        Assert.Equal("custom", registration.Name);
    }

    [Fact]
    public void AddServiceConnectProducer_TagsAndTimeoutAndFailureStatus_Propagate()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectProducer(
            tags: ["ready"],
            timeout: TimeSpan.FromSeconds(2),
            failureStatus: HealthStatus.Degraded);

        var registration = GetSingleRegistration(services);
        Assert.Contains("ready", registration.Tags);
        Assert.Equal(TimeSpan.FromSeconds(2), registration.Timeout);
        Assert.Equal(HealthStatus.Degraded, registration.FailureStatus);
    }

    [Fact]
    public void AddServiceConnectProducer_RegistrationFactory_ResolvesProducerConnectionHealthCheck()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IProducer>().Object);
        services.AddHealthChecks().AddServiceConnectProducer();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var registration = Assert.Single(options.Registrations);

        var instance = registration.Factory(provider);
        Assert.IsType<ProducerConnectionHealthCheck>(instance);
    }
}
