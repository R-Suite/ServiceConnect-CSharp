using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class MultiBusHealthCheckTests
{
    [Fact]
    public async Task FactoryOverload_ResolvesViaFactory()
    {
        var bus = Mock.Of<IBus>(b => b.IsConsuming == true);
        var services = new ServiceCollection();
        services.AddHealthChecks().AddServiceConnectBus("custom",
            sp => bus,
            HealthStatus.Unhealthy);

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var registration = options.Registrations.Single(r => r.Name == "custom");
        var check = (BusConsumingHealthCheck)registration.Factory(sp);
        var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = registration });

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task KeyedOverload_ResolvesByKey_DistinctBuses()
    {
        var busA = Mock.Of<IBus>(b => b.IsConsuming == true);
        var busB = Mock.Of<IBus>(b => b.IsConsuming == false);

        var services = new ServiceCollection();
        services.AddKeyedSingleton("A", busA);
        services.AddKeyedSingleton("B", busB);
        services.AddHealthChecks()
            .AddServiceConnectBus("checkA", "A", HealthStatus.Unhealthy)
            .AddServiceConnectBus("checkB", "B", HealthStatus.Unhealthy);

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var regA = options.Registrations.Single(r => r.Name == "checkA");
        var regB = options.Registrations.Single(r => r.Name == "checkB");

        var resultA = await ((BusConsumingHealthCheck)regA.Factory(sp))
            .CheckHealthAsync(new HealthCheckContext { Registration = regA });
        var resultB = await ((BusConsumingHealthCheck)regB.Factory(sp))
            .CheckHealthAsync(new HealthCheckContext { Registration = regB });

        Assert.Equal(HealthStatus.Healthy, resultA.Status);
        Assert.Equal(HealthStatus.Unhealthy, resultB.Status);
    }
}
