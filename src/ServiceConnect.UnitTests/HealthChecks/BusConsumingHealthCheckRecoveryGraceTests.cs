using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

/// <summary>
/// Recovery-grace coverage for <see cref="BusConsumingHealthCheck"/>. Pre-fix the check flipped
/// Unhealthy on any momentary <see cref="IBus.IsConsuming"/>=false observation, crash-looping
/// pods wired on liveness probes during broker auto-recovery. Post-fix a configurable grace
/// window after the most recent Healthy observation suppresses transient Unhealthy flips, while
/// a broker-cancelled short-circuit preserves immediate Unhealthy on permanent failures.
/// </summary>
public class BusConsumingHealthCheckRecoveryGraceTests
{
    private static HealthCheckContext Ctx(BusConsumingHealthCheck check) => new()
    {
        Registration = new HealthCheckRegistration("b", check, HealthStatus.Unhealthy, null),
    };

    [Fact]
    public async Task CheckHealthAsync_NeverHealthy_FlipsUnhealthyImmediately()
    {
        // First-probe-before-Healthy must be Unhealthy regardless of grace: a never-Healthy
        // consumer is genuinely unhealthy, not lazy.
        var bus = Mock.Of<IBus>(b => b.IsConsuming == false);
        var time = new FakeTimeProvider();
        var check = new BusConsumingHealthCheck(bus, consumer: null,
            recoveryGraceWindow: TimeSpan.FromSeconds(30), timeProvider: time);

        var result = await check.CheckHealthAsync(Ctx(check));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Bus is not consuming.", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_WithinGraceWindow_ReturnsHealthy()
    {
        var consuming = true;
        var bus = new Mock<IBus>();
        bus.Setup(b => b.IsConsuming).Returns(() => consuming);
        var time = new FakeTimeProvider();
        var check = new BusConsumingHealthCheck(bus.Object, consumer: null,
            recoveryGraceWindow: TimeSpan.FromSeconds(30), timeProvider: time);

        // First probe: bus is consuming → Healthy (stamps lastHealthy).
        var first = await check.CheckHealthAsync(Ctx(check));
        Assert.Equal(HealthStatus.Healthy, first.Status);

        // Bus disconnects.
        consuming = false;
        time.Advance(TimeSpan.FromSeconds(10));

        // Within grace → still Healthy with a recovery-grace note.
        var second = await check.CheckHealthAsync(Ctx(check));
        Assert.Equal(HealthStatus.Healthy, second.Status);
        Assert.Contains("recovery grace", second.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_BeyondGraceWindow_FlipsUnhealthy()
    {
        var consuming = true;
        var bus = new Mock<IBus>();
        bus.Setup(b => b.IsConsuming).Returns(() => consuming);
        var time = new FakeTimeProvider();
        var check = new BusConsumingHealthCheck(bus.Object, consumer: null,
            recoveryGraceWindow: TimeSpan.FromSeconds(30), timeProvider: time);

        await check.CheckHealthAsync(Ctx(check));  // stamps lastHealthy.

        consuming = false;
        time.Advance(TimeSpan.FromSeconds(31));

        var result = await check.CheckHealthAsync(Ctx(check));
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_BrokerCancelled_BypassesGrace()
    {
        var bus = new Mock<IBus>();
        bus.Setup(b => b.IsConsuming).Returns(true);
        var consumer = new Mock<IConsumer>();
        consumer.Setup(c => c.IsCancelledByBroker).Returns(false);
        var time = new FakeTimeProvider();
        var check = new BusConsumingHealthCheck(bus.Object, consumer.Object,
            recoveryGraceWindow: TimeSpan.FromSeconds(30), timeProvider: time);

        await check.CheckHealthAsync(Ctx(check));  // stamps lastHealthy.

        // Bus disconnects AND broker cancels — broker-cancelled short-circuit takes precedence.
        bus.Setup(b => b.IsConsuming).Returns(false);
        consumer.Setup(c => c.IsCancelledByBroker).Returns(true);
        time.Advance(TimeSpan.FromSeconds(5));  // well within grace.

        var result = await check.CheckHealthAsync(Ctx(check));
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("broker cancelled", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_HealthyAgain_RestampLastHealthy()
    {
        var consuming = true;
        var bus = new Mock<IBus>();
        bus.Setup(b => b.IsConsuming).Returns(() => consuming);
        var time = new FakeTimeProvider();
        var check = new BusConsumingHealthCheck(bus.Object, consumer: null,
            recoveryGraceWindow: TimeSpan.FromSeconds(30), timeProvider: time);

        await check.CheckHealthAsync(Ctx(check));  // T=0, stamps.

        // Disconnect at T=15 (grace branch), recover at T=20 (restamp), disconnect again at
        // T=40 (within 30s of T=20). Without restamping the second disconnect would be 40s
        // past the original T=0 stamp and fall outside grace.
        consuming = false;
        time.Advance(TimeSpan.FromSeconds(15));
        await check.CheckHealthAsync(Ctx(check));  // grace branch, lastHealthy unchanged.
        consuming = true;
        time.Advance(TimeSpan.FromSeconds(5));
        await check.CheckHealthAsync(Ctx(check));  // T=20, restamps.
        consuming = false;
        time.Advance(TimeSpan.FromSeconds(20));  // T=40, within 30s of T=20.
        var result = await check.CheckHealthAsync(Ctx(check));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("recovery grace", result.Description);
    }

    [Fact]
    public void Ctor_NegativeGrace_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BusConsumingHealthCheck(Mock.Of<IBus>(), consumer: null,
                recoveryGraceWindow: TimeSpan.FromSeconds(-1),
                timeProvider: TimeProvider.System));
    }

    [Fact]
    public async Task CheckHealthAsync_ZeroGrace_DisablesGracePath()
    {
        // ZeroGrace disables the grace path: the gate `_recoveryGraceWindow > TimeSpan.Zero`
        // is false, so the grace branch is skipped even after a Healthy stamp.
        var consuming = true;
        var bus = new Mock<IBus>();
        bus.Setup(b => b.IsConsuming).Returns(() => consuming);
        var time = new FakeTimeProvider();
        var check = new BusConsumingHealthCheck(bus.Object, consumer: null,
            recoveryGraceWindow: TimeSpan.Zero, timeProvider: time);

        await check.CheckHealthAsync(Ctx(check));  // Healthy, stamps lastHealthy.
        consuming = false;
        var result = await check.CheckHealthAsync(Ctx(check));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Bus is not consuming.", result.Description);
    }

    [Fact]
    public void Ctor_NullTimeProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BusConsumingHealthCheck(Mock.Of<IBus>(), consumer: null,
                recoveryGraceWindow: TimeSpan.FromSeconds(30),
                timeProvider: null!));
    }
}
