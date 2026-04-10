using Microsoft.Extensions.DependencyInjection;
using Moq;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

public class BusLifecycleTests
{
    private IBus CreateBus()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProducer>(new Mock<IProducer>().Object);
        services.AddServiceConnect(_ => { });

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IBus>();
    }

    [Fact]
    public void Bus_StartsAndStopsConsuming()
    {
        var bus = CreateBus();

        Assert.False(bus.IsConnected);

        bus.StartConsuming();
        Assert.True(bus.IsConnected);

        bus.StopConsuming();
        Assert.False(bus.IsConnected);
    }

    [Fact]
    public void Bus_DisposesCleanly()
    {
        var bus = CreateBus();

        bus.StartConsuming();
        Assert.True(bus.IsConnected);

        bus.Dispose();
        Assert.False(bus.IsConnected);
    }

    [Fact]
    public void Bus_DoubleDispose_DoesNotThrow()
    {
        var bus = CreateBus();

        bus.StartConsuming();

        var exception = Record.Exception(() =>
        {
            bus.Dispose();
            bus.Dispose();
        });

        Assert.Null(exception);
    }
}
