using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using ServiceConnect.HealthChecks;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.HealthChecks;

public class HealthCheckCancellationTests
{
    [Fact]
    public async Task BusConsumingHealthCheck_PreCancelledToken_Throws()
    {
        var bus = Mock.Of<IBus>(b => b.IsConsuming == true);
        var check = new BusConsumingHealthCheck(bus);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            check.CheckHealthAsync(new HealthCheckContext(), cts.Token));
    }

    [Fact]
    public async Task ConsumerConnectionHealthCheck_PreCancelledToken_Throws()
    {
        var consumer = Mock.Of<IConsumer>(c => c.IsConnected == true);
        var check = new ConsumerConnectionHealthCheck(consumer);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            check.CheckHealthAsync(new HealthCheckContext(), cts.Token));
    }

    [Fact]
    public async Task ProducerConnectionHealthCheck_PreCancelledToken_Throws()
    {
        var producer = Mock.Of<IProducer>(p => p.IsHealthy == true);
        var check = new ProducerConnectionHealthCheck(producer);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            check.CheckHealthAsync(new HealthCheckContext(), cts.Token));
    }
}
