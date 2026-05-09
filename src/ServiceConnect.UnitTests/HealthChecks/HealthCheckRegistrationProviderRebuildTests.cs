using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

/// <summary>
/// Pre-fix the registration's <c>cached</c> closure outlived the IServiceProvider that
/// resolved the original IBus/IConsumer/IProducer. A host that builds a fresh provider
/// re-runs the probe lambda but the lambda still observes the cached check from the
/// original provider's resolution — which has been disposed.
///
/// Post-fix the lambda resolves fresh from the supplied sp on every probe; two different
/// providers yield two different check instances.
/// </summary>
public class HealthCheckRegistrationProviderRebuildTests
{
    [Fact]
    public void AddServiceConnectBus_RebuiltProvider_ResolvesFreshCheck()
    {
        var bus1 = Mock.Of<IBus>(b => b.IsConsuming == true);
        var bus2 = Mock.Of<IBus>(b => b.IsConsuming == false);

        var registration = BuildRegistration(b =>
            b.AddServiceConnectBus("bus", sp => sp.GetRequiredService<IBus>()));

        var p1 = new ServiceCollection().AddSingleton(bus1).BuildServiceProvider();
        var p2 = new ServiceCollection().AddSingleton(bus2).BuildServiceProvider();
        var check1 = registration.Factory(p1);
        var check2 = registration.Factory(p2);

        Assert.IsType<BusConsumingHealthCheck>(check1);
        Assert.IsType<BusConsumingHealthCheck>(check2);
        // Pre-fix these would have been the SAME (cached) instance wrapping bus1 even
        // when probed via p2. Post-fix the registration factory always creates a fresh
        // wrapper; the IBus itself is cached by the IServiceProvider, not by us.
        Assert.NotSame(check1, check2);
    }

    [Fact]
    public void AddServiceConnectConsumer_RebuiltProvider_ResolvesFreshCheck()
    {
        var consumer1 = Mock.Of<IConsumer>(c => c.IsConnected == true);
        var consumer2 = Mock.Of<IConsumer>(c => c.IsConnected == false);

        var registration = BuildRegistration(b =>
            b.AddServiceConnectConsumer("c", sp => sp.GetRequiredService<IConsumer>()));

        var p1 = new ServiceCollection().AddSingleton(consumer1).BuildServiceProvider();
        var p2 = new ServiceCollection().AddSingleton(consumer2).BuildServiceProvider();
        var check1 = registration.Factory(p1);
        var check2 = registration.Factory(p2);

        Assert.IsType<ConsumerConnectionHealthCheck>(check1);
        Assert.IsType<ConsumerConnectionHealthCheck>(check2);
        Assert.NotSame(check1, check2);
    }

    [Fact]
    public void AddServiceConnectProducer_RebuiltProvider_ResolvesFreshCheck()
    {
        var producer1 = Mock.Of<IProducer>(p =>
            p.GetHealthSnapshot() == new ProducerHealthSnapshot(true, true));
        var producer2 = Mock.Of<IProducer>(p =>
            p.GetHealthSnapshot() == new ProducerHealthSnapshot(false, true));

        var registration = BuildRegistration(b =>
            b.AddServiceConnectProducer("p", sp => sp.GetRequiredService<IProducer>()));

        var p1 = new ServiceCollection().AddSingleton(producer1).BuildServiceProvider();
        var p2 = new ServiceCollection().AddSingleton(producer2).BuildServiceProvider();
        var check1 = registration.Factory(p1);
        var check2 = registration.Factory(p2);

        Assert.IsType<ProducerConnectionHealthCheck>(check1);
        Assert.IsType<ProducerConnectionHealthCheck>(check2);
        Assert.NotSame(check1, check2);
    }

    /// <summary>
    /// Builds a fresh ServiceCollection / IHealthChecksBuilder, applies the supplied
    /// registration callback, and returns the single registration produced. Lets each
    /// test exercise the factory lambda against multiple IServiceProvider instances
    /// without re-running the full ServiceCollection plumbing.
    /// </summary>
    private static HealthCheckRegistration BuildRegistration(Action<IHealthChecksBuilder> register)
    {
        var services = new ServiceCollection();
        var builder = services.AddHealthChecks();
        register(builder);

        // The registration is added to HealthCheckServiceOptions; pull it back out so
        // the factory can be exercised directly.
        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        return Assert.Single(options.Registrations);
    }
}
