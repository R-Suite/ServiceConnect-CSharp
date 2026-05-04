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
            mockConsumer.Setup(x => x.StartConsumingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ConsumerEventHandler>()))
                .Returns(Task.CompletedTask);
            services.AddSingleton<IConsumer>(mockConsumer.Object);
        }

        services.AddServiceConnect(b =>
            b.ConfigureBus(c => c.ScanForMessageHandlers = false)
             .ConfigureQueues(q => q.QueueName = "bus-lifecycle-test"));

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IBus>();
    }

    [Fact]
    public async Task Bus_StartsAndStopsConsuming_WithConsumer()
    {
        var bus = CreateBus(withConsumer: true);

        Assert.False(bus.IsConsuming);

        await bus.StartConsumingAsync();
        Assert.True(bus.IsConsuming);

        await bus.StopConsumingAsync();
        Assert.False(bus.IsConsuming);
    }

    [Fact]
    public async Task Bus_StartConsuming_ThrowsWithoutConsumer()
    {
        var bus = CreateBus(withConsumer: false);

        Assert.False(bus.IsConsuming);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bus.StartConsumingAsync());
    }

    [Fact]
    public async Task Bus_DisposesCleanly()
    {
        var bus = CreateBus(withConsumer: true);

        await bus.StartConsumingAsync();
        Assert.True(bus.IsConsuming);

        await bus.DisposeAsync();
        Assert.False(bus.IsConsuming);
    }

    [Fact]
    public async Task Bus_DoubleDispose_DoesNotThrow()
    {
        var bus = CreateBus(withConsumer: true);

        await bus.StartConsumingAsync();

        var exception = await Record.ExceptionAsync(async () =>
        {
            await bus.DisposeAsync();
            await bus.DisposeAsync();
        });

        Assert.Null(exception);
    }

    [Fact]
    public async Task Bus_StopIsTerminal_StartAfterStopThrows()
    {
        // StopConsumingAsync is terminal. A subsequent StartConsumingAsync
        // must throw with a clear message — the caller has to dispose and create a new Bus.
        var bus = CreateBus(withConsumer: true);

        await bus.StartConsumingAsync();
        await bus.StopConsumingAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => bus.StartConsumingAsync());
        Assert.Contains("stopped", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bus_DoubleStart_Throws()
    {
        // Start-while-already-consuming must throw, not silently replace state.
        var bus = CreateBus(withConsumer: true);

        await bus.StartConsumingAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => bus.StartConsumingAsync());
        Assert.Contains("consuming", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
