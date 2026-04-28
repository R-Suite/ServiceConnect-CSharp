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
}
