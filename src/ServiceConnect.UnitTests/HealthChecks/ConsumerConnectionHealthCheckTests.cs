using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class ConsumerConnectionHealthCheckTests
{
    private readonly Mock<IConsumer> _consumer = new();

    private ConsumerConnectionHealthCheck CreateSut() => new(_consumer.Object);

    [Fact]
    public async Task CheckHealthAsync_ConsumerConnected_ReturnsHealthy()
    {
        _consumer.SetupGet(c => c.IsConnected).Returns(true);

        var result = await CreateSut().CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("open", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckHealthAsync_ConsumerNotConnected_ReturnsUnhealthy()
    {
        _consumer.SetupGet(c => c.IsConnected).Returns(false);

        var ctx = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("x", _ => CreateSut(), HealthStatus.Unhealthy, null),
        };
        var result = await CreateSut().CheckHealthAsync(ctx);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("closed", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckHealthAsync_ConsumerNotConnected_HonoursDegradedFailureStatus()
    {
        _consumer.SetupGet(c => c.IsConnected).Returns(false);

        var ctx = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("x", _ => CreateSut(), HealthStatus.Degraded, null),
        };
        var result = await CreateSut().CheckHealthAsync(ctx);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task CheckHealthAsync_ConsumerNotConnected_NoRegistration_DefaultsToUnhealthy()
    {
        _consumer.SetupGet(c => c.IsConnected).Returns(false);

        // HealthCheckContext with null Registration — exercises the
        // `context.Registration?.FailureStatus ?? HealthStatus.Unhealthy` fallback path.
        var result = await CreateSut().CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
