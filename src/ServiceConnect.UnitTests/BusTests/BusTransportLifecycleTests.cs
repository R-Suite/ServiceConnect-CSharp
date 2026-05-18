using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.BusTests;

// IConsumer and IProducer are registered as DI singletons; their lifecycle is owned by the
// host's IServiceProvider, which disposes them on host shutdown. The Bus must NOT dispose
// either transport — doing so would double-dispose against DI's own teardown. These tests
// pin the new contract: Bus.StopConsumingAsync and Bus.DisposeAsync leave the transports
// alone.
public sealed class BusTransportLifecycleTests
{
    [Fact]
    public async Task DisposeAsync_DoesNotDisposeIConsumer()
    {
        var consumer = new Mock<IConsumer>();
        consumer.SetupGet(c => c.IsConnected).Returns(true);
        consumer.SetupGet(c => c.IsCancelledByBroker).Returns(false);
        consumer
            .Setup(c => c.StartConsumingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ConsumerEventHandler>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        int disposeCount = 0;
        consumer.Setup(c => c.DisposeAsync())
            .Callback(() => Interlocked.Increment(ref disposeCount))
            .Returns(ValueTask.CompletedTask);

        var bus = BuildBus(consumer.Object, producer: null);
        await bus.StartConsumingAsync();
        await bus.DisposeAsync();

        Assert.Equal(0, disposeCount);
    }

    [Fact]
    public async Task DisposeAsync_DoesNotDisposeIProducer()
    {
        var producer = new Mock<IProducer>();
        int disposeCount = 0;
        producer.Setup(p => p.DisposeAsync())
            .Callback(() => Interlocked.Increment(ref disposeCount))
            .Returns(ValueTask.CompletedTask);

        var bus = BuildBus(consumer: null, producer.Object);
        await bus.DisposeAsync();

        Assert.Equal(0, disposeCount);
    }

    [Fact]
    public async Task StopConsumingAsync_DoesNotDisposeIConsumer()
    {
        var consumer = new Mock<IConsumer>();
        consumer.SetupGet(c => c.IsConnected).Returns(true);
        consumer.SetupGet(c => c.IsCancelledByBroker).Returns(false);
        consumer
            .Setup(c => c.StartConsumingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ConsumerEventHandler>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        int disposeCount = 0;
        consumer.Setup(c => c.DisposeAsync())
            .Callback(() => Interlocked.Increment(ref disposeCount))
            .Returns(ValueTask.CompletedTask);

        var bus = BuildBus(consumer.Object, producer: null);
        await bus.StartConsumingAsync();
        await bus.StopConsumingAsync();

        Assert.Equal(0, disposeCount);
    }

    private static Bus BuildBus(IConsumer? consumer, IProducer? producer)
    {
        var serializer = new Mock<IMessageSerializer>();
        var filterPipeline = new Mock<IFilterPipeline>();
        var sendPipeline = new Mock<ISendMessagePipeline>();
        sendPipeline.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var requestReplyManager = new Mock<IRequestReplyManager>();
        var logger = new Mock<ILogger<Bus>>();
        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.Setup(q => q.QueueName).Returns("test-queue");
        var pipelineConfig = new Mock<IPipelineConfiguration>();
        pipelineConfig.Setup(p => p.OutgoingFilters).Returns([]);
        var dispatcher = new Mock<IMessageDispatcher>();
        var handlerReferences = new List<HandlerReference>();
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
            handlerReferences,
            pipelineConfig.Object,
            scopeFactory,
            scopeAccessor,
            consumer,
            producer);
    }
}
