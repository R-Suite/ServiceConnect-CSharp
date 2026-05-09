using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

/// <summary>
/// Post-M4+M6: the registration factory caches the wrapper per IServiceProvider via a
/// ConditionalWeakTable. Two probes against the SAME provider get the SAME wrapper —
/// preserving M4's recovery-grace state (instance-scoped <c>_lastHealthyTicks</c>) across
/// probes — while M6's IServiceProvider-rebuild contract is preserved by the table's
/// GC semantics: a rebuilt provider becomes unreachable, the cached wrapper is GC-eligible,
/// and the next probe against the new provider allocates a fresh wrapper. The pre-M6 fix
/// used a closure-captured cache that survived the SP rebuild; this composes M4's stable
/// state with M6's rebuild-aware contract via per-SP caching.
/// </summary>
public class HealthCheckRegistrationCachingTests
{
    [Fact]
    public void AddServiceConnectBus_TwoProbes_ReturnSameWrapperForSameProvider()
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

        // Same wrapper across probes against the same SP — M4 grace state stable.
        Assert.Same(first, second);
        Assert.IsType<BusConsumingHealthCheck>(first);
    }

    [Fact]
    public void AddServiceConnectConsumer_TwoProbes_ReturnSameWrapperForSameProvider()
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

        Assert.Same(first, second);
        Assert.IsType<ConsumerConnectionHealthCheck>(first);
    }

    [Fact]
    public void AddServiceConnectProducer_TwoProbes_ReturnSameWrapperForSameProvider()
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

        Assert.Same(first, second);
        Assert.IsType<ProducerConnectionHealthCheck>(first);
    }
}
