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
        // Post-M5: the check reads the (IsHealthy, HasAttemptedConnection) pair as a single
        // snapshot via GetHealthSnapshot. Set up the snapshot directly rather than the
        // individual properties.
        _producer.Setup(p => p.GetHealthSnapshot())
            .Returns(new ProducerHealthSnapshot(IsHealthy: true, HasAttemptedConnection: true));

        var result = await CreateSut().CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("open", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckHealthAsync_ProducerNotHealthy_ReturnsUnhealthy()
    {
        // HasAttemptedConnection = true: producer tried and failed, not just lazy.
        _producer.Setup(p => p.GetHealthSnapshot())
            .Returns(new ProducerHealthSnapshot(IsHealthy: false, HasAttemptedConnection: true));

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
        // HasAttemptedConnection = true: producer tried and failed, not just lazy.
        _producer.Setup(p => p.GetHealthSnapshot())
            .Returns(new ProducerHealthSnapshot(IsHealthy: false, HasAttemptedConnection: true));

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
        // HasAttemptedConnection = true: producer tried and failed, not just lazy.
        _producer.Setup(p => p.GetHealthSnapshot())
            .Returns(new ProducerHealthSnapshot(IsHealthy: false, HasAttemptedConnection: true));

        // HealthCheckContext with null Registration — exercises the
        // `context.Registration?.FailureStatus ?? HealthStatus.Unhealthy` fallback path.
        var result = await CreateSut().CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
