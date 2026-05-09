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

namespace ServiceConnect.UnitTests;

/// <summary>
/// Pins the ordering contract for <see cref="Bus.StartConsumingAsync"/>: <c>_consuming</c>
/// must be set to <c>true</c> BEFORE the broker's StartConsumingAsync await completes,
/// so that health probes during the startup window report Healthy. Setting the flag
/// only after the await would yield a spurious Unhealthy window.
/// </summary>
public class BusStartConsumingFlagOrderTests
{
    [Fact]
    public async Task StartConsumingAsync_DuringConsumerStart_IsConsumingReportsTrue()
    {
        // Drive a consumer whose StartConsumingAsync awaits until signalled. Verify that
        // while it's mid-await, bus.IsConsuming already returns true.
        var consumerStartedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerCanFinishTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var consumer = new Mock<IConsumer>();
        consumer.SetupGet(c => c.IsConnected).Returns(true);
        consumer.SetupGet(c => c.IsCancelledByBroker).Returns(false);
        consumer
            .Setup(c => c.StartConsumingAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<ConsumerEventHandler>(),
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                consumerStartedTcs.TrySetResult();
                await consumerCanFinishTcs.Task.ConfigureAwait(false);
            });

        var bus = BuildBus(consumer.Object);

        // Start consuming on a background task so the test can observe IsConsuming
        // while the consumer is still mid-await.
        var startTask = Task.Run(() => bus.StartConsumingAsync());
        await consumerStartedTcs.Task; // we are now mid-await inside the consumer

        // Pre-fix: IsConsuming returned false here (flag was set AFTER the await).
        // Post-fix: returns true (flag is set BEFORE the await).
        Assert.True(bus.IsConsuming);

        consumerCanFinishTcs.TrySetResult();
        await startTask;

        Assert.True(bus.IsConsuming);
    }

    [Fact]
    public async Task StartConsumingAsync_ConsumerStartFails_RollsBackIsConsumingFalse()
    {
        // Verify the catch block rolls _consuming back to false when the consumer's
        // StartConsumingAsync throws, so a failed start does not leave the bus
        // claiming to consume.
        var consumer = new Mock<IConsumer>();
        consumer.SetupGet(c => c.IsConnected).Returns(true);
        consumer.SetupGet(c => c.IsCancelledByBroker).Returns(false);
        consumer
            .Setup(c => c.StartConsumingAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<ConsumerEventHandler>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broker unreachable"));

        var bus = BuildBus(consumer.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => bus.StartConsumingAsync());
        Assert.False(bus.IsConsuming);
    }

    [Fact]
    public async Task StartConsumingAsync_ConsumerStartSucceeds_IsConsumingTrue()
    {
        // Happy-path sanity: after a successful StartConsumingAsync, IsConsuming is true.
        var consumer = new Mock<IConsumer>();
        consumer.SetupGet(c => c.IsConnected).Returns(true);
        consumer.SetupGet(c => c.IsCancelledByBroker).Returns(false);
        consumer
            .Setup(c => c.StartConsumingAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<ConsumerEventHandler>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var bus = BuildBus(consumer.Object);

        await bus.StartConsumingAsync();

        Assert.True(bus.IsConsuming);
    }

    private static Bus BuildBus(IConsumer consumer)
    {
        var serializer = new Mock<IMessageSerializer>();
        var filterPipeline = new Mock<IFilterPipeline>();
        var sendPipeline = new Mock<ISendMessagePipeline>();
        sendPipeline.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var requestReplyManager = new Mock<IRequestReplyManager>();
        var logger = new Mock<ILogger<Bus>>();
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
            logger.Object,
            queueConfig.Object,
            dispatcher.Object,
            [],
            pipelineConfig.Object,
            scopeFactory,
            scopeAccessor,
            consumer);
    }
}
