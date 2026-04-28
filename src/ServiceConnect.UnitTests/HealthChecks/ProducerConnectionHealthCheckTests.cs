using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class ProducerConnectionHealthCheckTests
{
    private readonly Mock<IProducer> _producer = new();

    private ProducerConnectionHealthCheck CreateSut() => new(_producer.Object);

    [Fact]
    public async Task CheckHealthAsync_ProducerHealthy_ReturnsHealthy()
    {
        _producer.SetupGet(p => p.IsHealthy).Returns(true);

        var result = await CreateSut().CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("open", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckHealthAsync_ProducerNotHealthy_ReturnsUnhealthy()
    {
        _producer.SetupGet(p => p.IsHealthy).Returns(false);

        var ctx = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("x", _ => CreateSut(), HealthStatus.Unhealthy, null),
        };
        var result = await CreateSut().CheckHealthAsync(ctx);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("closed", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckHealthAsync_ProducerNotHealthy_HonoursDegradedFailureStatus()
    {
        _producer.SetupGet(p => p.IsHealthy).Returns(false);

        var ctx = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("x", _ => CreateSut(), HealthStatus.Degraded, null),
        };
        var result = await CreateSut().CheckHealthAsync(ctx);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_ProducerNotHealthy_NoRegistration_DefaultsToUnhealthy()
    {
        _producer.SetupGet(p => p.IsHealthy).Returns(false);

        // HealthCheckContext with null Registration — exercises the
        // `context.Registration?.FailureStatus ?? HealthStatus.Unhealthy` fallback path.
        var result = await CreateSut().CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
