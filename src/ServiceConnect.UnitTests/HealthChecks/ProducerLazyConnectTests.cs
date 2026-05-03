using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class ProducerLazyConnectTests
{
    [Fact]
    public async Task NotYetAttempted_ReturnsHealthy()
    {
        var producer = Mock.Of<IProducer>(p =>
            p.IsHealthy == false &&
            p.HasAttemptedConnection == false);
        var check = new ProducerConnectionHealthCheck(producer);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("not yet attempted", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AttemptedAndDisconnected_ReturnsUnhealthy()
    {
        var producer = Mock.Of<IProducer>(p =>
            p.IsHealthy == false &&
            p.HasAttemptedConnection == true);
        var check = new ProducerConnectionHealthCheck(producer);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Connected_ReturnsHealthy()
    {
        var producer = Mock.Of<IProducer>(p =>
            p.IsHealthy == true &&
            p.HasAttemptedConnection == true);
        var check = new ProducerConnectionHealthCheck(producer);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}
