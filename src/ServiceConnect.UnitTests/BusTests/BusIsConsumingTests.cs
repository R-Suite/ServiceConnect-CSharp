using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.BusTests;

/// <summary>
/// Verifies the broker-cancel short-circuit on <see cref="Bus.IsConsuming"/>:
/// when the consumer's <see cref="IConsumer.IsCancelledByBroker"/> returns true
/// the bus reports IsConsuming = false even though <c>_consuming</c> is still set.
/// This is the load-bearing invariant <c>BusConsumingHealthCheck</c> relies on
/// to flip Unhealthy after a broker-initiated basic.cancel.
/// </summary>
public class BusIsConsumingTests
{
    [Fact]
    public async Task IsConsuming_BrokerCancel_ReturnsFalse()
    {
        var consumer = new Mock<IConsumer>();
        consumer.Setup(c => c.StartConsumingAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<ConsumerEventHandler>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumer.SetupGet(c => c.IsConnected).Returns(true);
        consumer.SetupGet(c => c.IsCancelledByBroker).Returns(true);

        var bus = CreateBus(consumer.Object);

        await bus.StartConsumingAsync();

        // _consuming is true (StartConsumingAsync completed), but IsCancelledByBroker
        // overrides — the public IsConsuming must read false.
        Assert.False(bus.IsConsuming);
    }

    [Fact]
    public async Task IsConsuming_NotCancelledByBroker_ReturnsTrueWhenConsuming()
    {
        var consumer = new Mock<IConsumer>();
        consumer.Setup(c => c.StartConsumingAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<ConsumerEventHandler>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumer.SetupGet(c => c.IsConnected).Returns(true);
        consumer.SetupGet(c => c.IsCancelledByBroker).Returns(false);

        var bus = CreateBus(consumer.Object);

        await bus.StartConsumingAsync();

        Assert.True(bus.IsConsuming);
    }

    [Fact]
    public void IsConsuming_NoConsumerRegistered_ReturnsFalse()
    {
        // Defensive coverage of the `_consumer?.IsCancelledByBroker ?? false` null-coalesce path:
        // a Bus with no consumer must not NRE when IsConsuming is read, even before any start.
        var bus = CreateBus(consumer: null);

        Assert.False(bus.IsConsuming);
    }

    [Fact]
    public void IsConsuming_BrokerCancelFlipsAfterStart_ReturnsFalse()
    {
        // Race-free via reflection: simulates the operational sequence where the bus
        // started consuming healthily, then later the broker cancels. The IsConsuming
        // getter must observe the new state on the next read with no Stop in between.
        var brokerCancel = false;
        var consumer = new Mock<IConsumer>();
        consumer.Setup(c => c.StartConsumingAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<ConsumerEventHandler>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumer.SetupGet(c => c.IsCancelledByBroker).Returns(() => brokerCancel);

        var bus = CreateBus(consumer.Object);
        SetConsumingFlag(bus, true);

        Assert.True(bus.IsConsuming);

        brokerCancel = true;
        Assert.False(bus.IsConsuming);
    }

    private static Bus CreateBus(IConsumer? consumer)
    {
        var serializer = new Mock<IMessageSerializer>();
        var filterPipeline = new Mock<IFilterPipeline>();
        var sendPipeline = new Mock<ISendMessagePipeline>();
        var requestReplyManager = new Mock<IRequestReplyManager>();
        var logger = new Mock<ILogger<Bus>>();
        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("test-queue");
        var dispatcher = new Mock<IMessageDispatcher>();
        var pipelineConfig = new Mock<IPipelineConfiguration>();
        pipelineConfig.SetupGet(p => p.OutgoingFilters).Returns([]);
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var scopeAccessor = new ConsumeScopeAccessor();

        return new Bus(
            serializer.Object,
            filterPipeline.Object,
            sendPipeline.Object,
            requestReplyManager.Object,
            logger.Object,
            queueConfig.Object,
            dispatcher.Object,
            [],
            pipelineConfig.Object,
            scopeFactory,
            scopeAccessor,
            consumer);
    }

    private static void SetConsumingFlag(Bus bus, bool value)
    {
        var field = typeof(Bus).GetField("_consuming",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(bus, value);
    }
}
