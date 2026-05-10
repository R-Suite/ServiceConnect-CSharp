using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Verifies that <see cref="Bus.StopConsumingAsync"/> drives the transport-level graceful
/// stop via <see cref="IConsumer.StopConsumingAsync"/>. Without that call the broker
/// keeps delivering until DI disposes the consumer (which can be arbitrarily later than
/// <c>BusHostedService.StopAsync</c> returns), and the dispatch pipeline keeps running
/// in the gap.
/// </summary>
public class BusStopConsumingTransportHookTests
{
    [Fact]
    public async Task StopConsumingAsync_AfterStart_InvokesConsumerStopConsumingAsync()
    {
        var consumer = new Mock<IConsumer>();
        consumer.Setup(c => c.StartConsumingAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<ConsumerEventHandler>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumer.Setup(c => c.StopConsumingAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var bus = BuildBus(consumer.Object);

        await bus.StartConsumingAsync();
        await bus.StopConsumingAsync();

        consumer.Verify(c => c.StopConsumingAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StopConsumingAsync_NotConsuming_DoesNotInvokeConsumerStop()
    {
        var consumer = new Mock<IConsumer>();
        var bus = BuildBus(consumer.Object);

        // Bus has not started consuming. Stop is a no-op; the transport hook should not
        // fire (no consumer to stop).
        await bus.StopConsumingAsync();

        consumer.Verify(c => c.StopConsumingAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StopConsumingAsync_ConsumerStopThrows_LogsWarningAndDoesNotPropagate()
    {
        var consumer = new Mock<IConsumer>();
        consumer.Setup(c => c.StartConsumingAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<ConsumerEventHandler>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumer.Setup(c => c.StopConsumingAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport down"));

        var loggerMock = new Mock<ILogger<Bus>>();
        var bus = BuildBus(consumer.Object, loggerMock.Object);

        await bus.StartConsumingAsync();

        // Swallowed: a transport-side stop failure must not prevent the bus from
        // recording its stopped state.
        await bus.StopConsumingAsync();

        loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("StopConsumingAsync")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task StopConsumingAsync_PropagatesCallerCancellation()
    {
        // The caller's cancellation token must reach IConsumer.StopConsumingAsync so a
        // wedged transport-level drain can be aborted by host-shutdown timeout.
        var consumer = new Mock<IConsumer>();
        consumer.Setup(c => c.StartConsumingAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<ConsumerEventHandler>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        CancellationToken observedToken = default;
        consumer.Setup(c => c.StopConsumingAsync(It.IsAny<CancellationToken>()))
            .Callback<CancellationToken>(ct => observedToken = ct)
            .Returns(Task.CompletedTask);

        var bus = BuildBus(consumer.Object);
        await bus.StartConsumingAsync();

        using var cts = new CancellationTokenSource();
        await bus.StopConsumingAsync(cts.Token);

        Assert.Equal(cts.Token, observedToken);
    }

    private static Bus BuildBus(IConsumer? consumer = null, ILogger<Bus>? logger = null)
    {
        var serializer = new Mock<IMessageSerializer>();
        var filterPipeline = new Mock<IFilterPipeline>();
        var sendPipeline = new Mock<ISendMessagePipeline>();
        sendPipeline.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var requestReplyManager = new Mock<IRequestReplyManager>();
        var loggerObj = logger ?? new Mock<ILogger<Bus>>().Object;
        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("test-queue");
        var pipelineConfig = new Mock<IPipelineConfiguration>();
        pipelineConfig.SetupGet(p => p.OutgoingFilters).Returns([]);
        var dispatcher = new Mock<IMessageDispatcher>();
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var scopeAccessor = new ConsumeScopeAccessor();

        return new Bus(
            serializer.Object,
            filterPipeline.Object,
            sendPipeline.Object,
            requestReplyManager.Object,
            loggerObj,
            queueConfig.Object,
            dispatcher.Object,
            [],
            pipelineConfig.Object,
            scopeFactory,
            scopeAccessor,
            consumer: consumer);
    }
}
