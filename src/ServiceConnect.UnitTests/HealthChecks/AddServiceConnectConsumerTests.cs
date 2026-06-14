using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class AddServiceConnectConsumerTests
{
    private static HealthCheckRegistration GetSingleRegistration(IServiceCollection services)
    {
        services.AddSingleton(new Mock<IConsumer>().Object);
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        return Assert.Single(options.Registrations);
    }

    [Fact]
    public void AddServiceConnectConsumer_DefaultName_IsServiceConnectConsumer()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectConsumer();

        var registration = GetSingleRegistration(services);
        Assert.Equal("serviceconnect-consumer", registration.Name);
    }

    [Fact]
    public void AddServiceConnectConsumer_CustomName_Propagates()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectConsumer(name: "custom");

        var registration = GetSingleRegistration(services);
        Assert.Equal("custom", registration.Name);
    }

    [Fact]
    public void AddServiceConnectConsumer_TagsAndTimeoutAndFailureStatus_Propagate()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectConsumer(
            tags: ["ready"],
            timeout: TimeSpan.FromSeconds(2),
            failureStatus: HealthStatus.Degraded);

        var registration = GetSingleRegistration(services);
        Assert.Contains("ready", registration.Tags);
        Assert.Equal(TimeSpan.FromSeconds(2), registration.Timeout);
        Assert.Equal(HealthStatus.Degraded, registration.FailureStatus);
    }

    [Fact]
    public void AddServiceConnectConsumer_RegistrationFactory_ResolvesConsumerConnectionHealthCheck()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IConsumer>().Object);
        services.AddHealthChecks().AddServiceConnectConsumer();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var registration = Assert.Single(options.Registrations);

        var instance = registration.Factory(provider);
        Assert.IsType<ConsumerConnectionHealthCheck>(instance);
    }
}
