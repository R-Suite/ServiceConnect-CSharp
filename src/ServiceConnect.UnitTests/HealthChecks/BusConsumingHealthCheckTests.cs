using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class BusConsumingHealthCheckTests
{
    private readonly Mock<IBus> _bus = new();

    private BusConsumingHealthCheck CreateSut() => new(_bus.Object);

    [Fact]
    public async Task CheckHealthAsync_BusIsConsuming_ReturnsHealthy()
    {
        _bus.SetupGet(b => b.IsConsuming).Returns(true);

        var result = await CreateSut().CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("consuming", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckHealthAsync_BusIsNotConsuming_ReturnsUnhealthy()
    {
        _bus.SetupGet(b => b.IsConsuming).Returns(false);

        var ctx = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("x", _ => CreateSut(), HealthStatus.Unhealthy, null),
        };
        var result = await CreateSut().CheckHealthAsync(ctx);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not consuming", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckHealthAsync_BusIsNotConsuming_HonoursDegradedFailureStatus()
    {
        _bus.SetupGet(b => b.IsConsuming).Returns(false);

        var ctx = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("x", _ => CreateSut(), HealthStatus.Degraded, null),
        };
        var result = await CreateSut().CheckHealthAsync(ctx);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_BusIsNotConsuming_NoRegistration_DefaultsToUnhealthy()
    {
        _bus.SetupGet(b => b.IsConsuming).Returns(false);

        // HealthCheckContext with null Registration — exercises the
        // `context.Registration?.FailureStatus ?? HealthStatus.Unhealthy` fallback path.
        var result = await CreateSut().CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_BusReportsConsumingFalse_DueToBrokerCancel_ReturnsUnhealthy()
    {
        // The broker-cancel side is exercised at Bus / Consumer level — see BusIsConsumingTests
        // and RabbitMqConsumerHostBrokerCancelTests. From the health-check's vantage the only
        // observable is IsConsuming = false; this test asserts the existing mapping still holds
        // for that path so the integrated chain (host broker-cancel → Consumer.IsCancelledByBroker
        // → Bus.IsConsuming = false → BusConsumingHealthCheck Unhealthy) is end-to-end covered.
        _bus.SetupGet(b => b.IsConsuming).Returns(false);

        var ctx = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("bus-consuming", _ => CreateSut(), HealthStatus.Unhealthy, null),
        };
        var result = await CreateSut().CheckHealthAsync(ctx);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not consuming", result.Description, StringComparison.OrdinalIgnoreCase);
    }
}
