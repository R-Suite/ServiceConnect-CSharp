using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

/// <summary>
/// Recovery-grace coverage for <see cref="ConsumerConnectionHealthCheck"/>. Mirrors the
/// <see cref="BusConsumingHealthCheckRecoveryGraceTests"/> shape but observes
/// <see cref="IConsumer.IsConnected"/>/<see cref="IConsumer.IsCancelledByBroker"/> directly.
/// </summary>
public class ConsumerConnectionHealthCheckRecoveryGraceTests
{
    private static HealthCheckContext Ctx(ConsumerConnectionHealthCheck check) => new()
    {
        Registration = new HealthCheckRegistration("c", check, HealthStatus.Unhealthy, null),
    };

    [Fact]
    public async Task CheckHealthAsync_NeverHealthy_FlipsUnhealthyImmediately()
    {
        var consumer = new Mock<IConsumer>();
        consumer.Setup(c => c.IsConnected).Returns(false);
        consumer.Setup(c => c.IsCancelledByBroker).Returns(false);
        var time = new FakeTimeProvider();
        var check = new ConsumerConnectionHealthCheck(consumer.Object,
            recoveryGraceWindow: TimeSpan.FromSeconds(30), timeProvider: time);

        var result = await check.CheckHealthAsync(Ctx(check));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Consumer connection is closed.", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_WithinGraceWindow_ReturnsHealthy()
    {
        var connected = true;
        var consumer = new Mock<IConsumer>();
        consumer.Setup(c => c.IsConnected).Returns(() => connected);
        consumer.Setup(c => c.IsCancelledByBroker).Returns(false);
        var time = new FakeTimeProvider();
        var check = new ConsumerConnectionHealthCheck(consumer.Object,
            recoveryGraceWindow: TimeSpan.FromSeconds(30), timeProvider: time);

        var first = await check.CheckHealthAsync(Ctx(check));
        Assert.Equal(HealthStatus.Healthy, first.Status);

        connected = false;
        time.Advance(TimeSpan.FromSeconds(10));

        var second = await check.CheckHealthAsync(Ctx(check));
        Assert.Equal(HealthStatus.Healthy, second.Status);
        Assert.Contains("recovery grace", second.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_BeyondGraceWindow_FlipsUnhealthy()
    {
        var connected = true;
        var consumer = new Mock<IConsumer>();
        consumer.Setup(c => c.IsConnected).Returns(() => connected);
        consumer.Setup(c => c.IsCancelledByBroker).Returns(false);
        var time = new FakeTimeProvider();
        var check = new ConsumerConnectionHealthCheck(consumer.Object,
            recoveryGraceWindow: TimeSpan.FromSeconds(30), timeProvider: time);

        await check.CheckHealthAsync(Ctx(check));  // stamps lastHealthy.

        connected = false;
        time.Advance(TimeSpan.FromSeconds(31));

        var result = await check.CheckHealthAsync(Ctx(check));
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_BrokerCancelled_BypassesGrace()
    {
        var connected = true;
        var cancelled = false;
        var consumer = new Mock<IConsumer>();
        consumer.Setup(c => c.IsConnected).Returns(() => connected);
        consumer.Setup(c => c.IsCancelledByBroker).Returns(() => cancelled);
        var time = new FakeTimeProvider();
        var check = new ConsumerConnectionHealthCheck(consumer.Object,
            recoveryGraceWindow: TimeSpan.FromSeconds(30), timeProvider: time);

        await check.CheckHealthAsync(Ctx(check));  // stamps lastHealthy.

        // Disconnect AND broker cancels — broker-cancelled short-circuit takes precedence.
        connected = false;
        cancelled = true;
        time.Advance(TimeSpan.FromSeconds(5));  // well within grace.

        var result = await check.CheckHealthAsync(Ctx(check));
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("broker cancelled", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_HealthyAgain_RestampLastHealthy()
    {
        var connected = true;
        var consumer = new Mock<IConsumer>();
        consumer.Setup(c => c.IsConnected).Returns(() => connected);
        consumer.Setup(c => c.IsCancelledByBroker).Returns(false);
        var time = new FakeTimeProvider();
        var check = new ConsumerConnectionHealthCheck(consumer.Object,
            recoveryGraceWindow: TimeSpan.FromSeconds(30), timeProvider: time);

        await check.CheckHealthAsync(Ctx(check));  // T=0, stamps.

        connected = false;
        time.Advance(TimeSpan.FromSeconds(15));
        await check.CheckHealthAsync(Ctx(check));  // grace branch, lastHealthy unchanged.
        connected = true;
        time.Advance(TimeSpan.FromSeconds(5));
        await check.CheckHealthAsync(Ctx(check));  // T=20, restamps.
        connected = false;
        time.Advance(TimeSpan.FromSeconds(20));  // T=40, within 30s of T=20.
        var result = await check.CheckHealthAsync(Ctx(check));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("recovery grace", result.Description);
    }

    [Fact]
    public void Ctor_NegativeGrace_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ConsumerConnectionHealthCheck(Mock.Of<IConsumer>(),
                recoveryGraceWindow: TimeSpan.FromSeconds(-1),
                timeProvider: TimeProvider.System));
    }

    [Fact]
    public async Task CheckHealthAsync_ZeroGrace_DisablesGracePath()
    {
        var connected = true;
        var consumer = new Mock<IConsumer>();
        consumer.Setup(c => c.IsConnected).Returns(() => connected);
        consumer.Setup(c => c.IsCancelledByBroker).Returns(false);
        var time = new FakeTimeProvider();
        var check = new ConsumerConnectionHealthCheck(consumer.Object,
            recoveryGraceWindow: TimeSpan.Zero, timeProvider: time);

        await check.CheckHealthAsync(Ctx(check));  // Healthy, stamps lastHealthy.
        connected = false;
        var result = await check.CheckHealthAsync(Ctx(check));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Consumer connection is closed.", result.Description);
    }

    [Fact]
    public void Ctor_NullTimeProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ConsumerConnectionHealthCheck(Mock.Of<IConsumer>(),
                recoveryGraceWindow: TimeSpan.FromSeconds(30),
                timeProvider: null!));
    }
}
