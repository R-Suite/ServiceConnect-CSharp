using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class ProducerLazyConnectTests
{
    // ProducerConnectionHealthCheck reads the (IsHealthy, HasAttemptedConnection) pair
    // atomically via IProducer.GetHealthSnapshot. Mocks set up the snapshot directly so
    // the test exercises the snapshot contract rather than the (unused) individual
    // property reads.

    [Fact]
    public async Task NotYetAttempted_ReturnsHealthy()
    {
        var producer = Mock.Of<IProducer>(p =>
            p.GetHealthSnapshot() == new ProducerHealthSnapshot(false, false));
        var check = new ProducerConnectionHealthCheck(producer);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("not yet attempted", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AttemptedAndDisconnected_ReturnsUnhealthy()
    {
        var producer = Mock.Of<IProducer>(p =>
            p.GetHealthSnapshot() == new ProducerHealthSnapshot(false, true));
        var check = new ProducerConnectionHealthCheck(producer);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Connected_ReturnsHealthy()
    {
        var producer = Mock.Of<IProducer>(p =>
            p.GetHealthSnapshot() == new ProducerHealthSnapshot(true, true));
        var check = new ProducerConnectionHealthCheck(producer);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}
