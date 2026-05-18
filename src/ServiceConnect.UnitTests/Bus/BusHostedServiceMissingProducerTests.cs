using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using Xunit;

// Namespace deliberately does NOT match the folder name to avoid shadowing the
// ServiceConnect.Bus type for sibling tests under ServiceConnect.UnitTests.*.
namespace ServiceConnect.UnitTests.BusInterface;

public sealed class BusHostedServiceMissingProducerTests
{
    [Fact]
    public async Task StartAsync_NoProducer_DefaultAllowMissingProducer_Throws()
    {
        var bus = new Mock<IBus>(MockBehavior.Strict);
        bus.Setup(b => b.StartConsumingAsync(It.IsAny<System.Threading.CancellationToken>()))
           .Returns(System.Threading.Tasks.Task.CompletedTask);

        var config = new BusConfiguration { AutoStartConsuming = false };
        var transport = new TransportConfiguration();

        var hostedService = new BusHostedService(
            bus.Object,
            config,
            transport,
            NullLogger<BusHostedService>.Instance,
            scanWarnings: null,
            producer: null);

        var ex = await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => hostedService.StartAsync(System.Threading.CancellationToken.None));

        Assert.Contains("IProducer", ex.Message, System.StringComparison.Ordinal);
        Assert.Contains("AllowMissingProducer", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_NoProducer_AllowMissingProducerTrue_Succeeds()
    {
        var bus = new Mock<IBus>(MockBehavior.Loose);
        bus.Setup(b => b.StartConsumingAsync(It.IsAny<System.Threading.CancellationToken>()))
           .Returns(System.Threading.Tasks.Task.CompletedTask);

        var config = new BusConfiguration { AutoStartConsuming = false, AllowMissingProducer = true };
        var transport = new TransportConfiguration();

        var hostedService = new BusHostedService(
            bus.Object,
            config,
            transport,
            NullLogger<BusHostedService>.Instance,
            scanWarnings: null,
            producer: null);

        // Must not throw.
        await hostedService.StartAsync(System.Threading.CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ProducerPresent_DefaultFlags_Succeeds()
    {
        var bus = new Mock<IBus>(MockBehavior.Loose);
        bus.Setup(b => b.StartConsumingAsync(It.IsAny<System.Threading.CancellationToken>()))
           .Returns(System.Threading.Tasks.Task.CompletedTask);

        var producer = new Mock<IProducer>().Object;
        var config = new BusConfiguration { AutoStartConsuming = false };
        var transport = new TransportConfiguration();

        var hostedService = new BusHostedService(
            bus.Object,
            config,
            transport,
            NullLogger<BusHostedService>.Instance,
            scanWarnings: null,
            producer: producer);

        await hostedService.StartAsync(System.Threading.CancellationToken.None);
    }
}
