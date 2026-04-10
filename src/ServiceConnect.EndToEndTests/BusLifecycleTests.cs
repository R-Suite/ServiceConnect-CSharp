using Microsoft.Extensions.DependencyInjection;
using Moq;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

public class BusLifecycleTests
{
    private IBus CreateBus(bool withConsumer = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProducer>(new Mock<IProducer>().Object);

        if (withConsumer)
        {
            var mockConsumer = new Mock<IConsumer>();
            mockConsumer.Setup(x => x.StartConsumingAsync(It.IsAny<string>(), It.IsAny<IList<string>>(), It.IsAny<ConsumerEventHandler>()))
                .Returns(Task.CompletedTask);
            services.AddSingleton<IConsumer>(mockConsumer.Object);
        }

        services.AddServiceConnect(_ => { });

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IBus>();
    }

    [Fact]
    public void Bus_StartsAndStopsConsuming_WithConsumer()
    {
        var bus = CreateBus(withConsumer: true);

        Assert.False(bus.IsConnected);

        bus.StartConsuming();
        Assert.True(bus.IsConnected);

        bus.StopConsuming();
        Assert.False(bus.IsConnected);
    }

    [Fact]
    public void Bus_StartConsuming_ThrowsWithoutConsumer()
    {
        var bus = CreateBus(withConsumer: false);

        Assert.False(bus.IsConnected);
        Assert.Throws<InvalidOperationException>(() => bus.StartConsuming());
    }

    [Fact]
    public void Bus_DisposesCleanly()
    {
        var bus = CreateBus(withConsumer: true);

        bus.StartConsuming();
        Assert.True(bus.IsConnected);

        bus.Dispose();
        Assert.False(bus.IsConnected);
    }

    [Fact]
    public void Bus_DoubleDispose_DoesNotThrow()
    {
        var bus = CreateBus(withConsumer: true);

        bus.StartConsuming();

        var exception = Record.Exception(() =>
        {
            bus.Dispose();
            bus.Dispose();
        });

        Assert.Null(exception);
    }
}
