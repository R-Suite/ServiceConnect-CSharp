using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

/// <summary>
/// Verifies that the parameterless-grace <c>AddServiceConnectBus(name, busFactory, ...)</c>
/// overload pulls <see cref="IConsumer"/> from the service provider so the broker-cancelled
/// short-circuit fires for third-party <see cref="IBus"/> implementations that don't
/// override the <c>IsCancelledByBroker</c> default-interface-method. Without that, a
/// permanent broker-cancellation on a custom bus would sit in the recovery grace window
/// indefinitely (the DIM returns <see langword="false"/>; the bus's <c>IsConsuming</c>
/// is false; recovery grace says "Healthy until age &gt; window").
/// </summary>
public class HealthChecksBuilderExtensionsThirdPartyBusTests
{
    private static HealthCheckRegistration GetRegistration(IServiceProvider sp, string name)
    {
        var options = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        return options.Registrations.First(r => r.Name == name);
    }

    [Fact]
    public async Task AddServiceConnectBus_FactoryOverload_ResolvesIConsumer_ForBrokerCancelShortCircuit()
    {
        // The bus is "not consuming" and a Mock<IBus> with no IsCancelledByBroker setup
        // returns the DIM default (false), modelling a third-party transport. A correctly-
        // wired IConsumer in DI flips IsCancelledByBroker=true so the broker-cancel branch
        // fires immediately even though the recovery-grace window has not elapsed.
        var bus = new Mock<IBus>();
        bus.SetupGet(b => b.IsConsuming).Returns(true); // first probe is Healthy

        var consumer = new Mock<IConsumer>();
        consumer.SetupGet(c => c.IsCancelledByBroker).Returns(true);

        var services = new ServiceCollection();
        services.AddSingleton<IBus>(bus.Object);
        services.AddSingleton(consumer.Object);
        services.AddHealthChecks()
            .AddServiceConnectBus(
                name: "bus",
                busFactory: sp => sp.GetRequiredService<IBus>());
        var sp = services.BuildServiceProvider();

        var registration = GetRegistration(sp, "bus");
        var check = registration.Factory(sp);
        var ctx = new HealthCheckContext { Registration = registration };

        // Stamp a Healthy observation first so the grace window would otherwise apply on
        // the next probe.
        await check.CheckHealthAsync(ctx);

        // Now simulate the bus losing IsConsuming; the consumer signals broker-cancel.
        bus.SetupGet(b => b.IsConsuming).Returns(false);
        var result = await check.CheckHealthAsync(ctx);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("broker cancelled", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddServiceConnectBus_FactoryOverload_NoConsumerInDi_FallsBackToBusDimAndStaysInGrace()
    {
        // Without an IConsumer registered AND with a third-party IBus that doesn't override
        // IsCancelledByBroker, the check has no broker-cancel signal and falls into the
        // recovery grace window after a Healthy observation. This pins the documented
        // behaviour: third-party hosts must register either an IConsumer or a custom IBus
        // that overrides IsCancelledByBroker to get broker-cancel short-circuiting.
        var bus = new Mock<IBus>();
        bus.SetupGet(b => b.IsConsuming).Returns(true);

        var services = new ServiceCollection();
        services.AddSingleton<IBus>(bus.Object);
        // Intentionally no IConsumer.
        services.AddHealthChecks()
            .AddServiceConnectBus(
                name: "bus",
                busFactory: sp => sp.GetRequiredService<IBus>());
        var sp = services.BuildServiceProvider();

        var registration = GetRegistration(sp, "bus");
        var check = registration.Factory(sp);
        var ctx = new HealthCheckContext { Registration = registration };

        var first = await check.CheckHealthAsync(ctx);
        Assert.Equal(HealthStatus.Healthy, first.Status);

        // Simulate the bus losing IsConsuming. Without a consumer signal AND with no DIM
        // override, the check sits in grace and returns Healthy.
        bus.SetupGet(b => b.IsConsuming).Returns(false);
        var second = await check.CheckHealthAsync(ctx);
        Assert.Equal(HealthStatus.Healthy, second.Status);
        Assert.Contains("recovery grace", second.Description, StringComparison.OrdinalIgnoreCase);
    }
}
