using System.Collections.Generic;
using System.Reflection;
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

// Regression coverage for H14: StopConsumingCoreAsync must rethrow OCE without
// mutating _consuming/_stopped. The structural fix (all mutation sits behind
// _lifecycleSemaphore.WaitAsync, which propagates OCE before the body runs)
// was introduced in the Task-12 simplification. These tests lock that invariant in.
public sealed class BusLifecycleCancellationTests
{
    [Fact]
    public async Task StopConsumingAsync_TokenCancelledBeforeSemaphoreAcquired_ThrowsOceWithoutMutatingState()
    {
        var bus = BuildBus();
        await bus.StartConsumingAsync();

        // Hold _lifecycleSemaphore from outside so the next StopConsumingAsync blocks at WaitAsync.
        var semField = typeof(Bus).GetField("_lifecycleSemaphore",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var sem = (SemaphoreSlim)semField!.GetValue(bus)!;
        await sem.WaitAsync();

        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel(); // cancel before the wait can complete

            // TaskCanceledException is a subclass of OperationCanceledException; ThrowsAnyAsync
            // accepts the full hierarchy, which is the correct assertion here.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => bus.StopConsumingAsync(cts.Token));

            // _consuming must still be true — no mutation occurred under cancellation.
            Assert.True(bus.IsConsuming);
        }
        finally
        {
            sem.Release();
        }

        // A subsequent uncontested StopConsumingAsync must succeed normally.
        await bus.StopConsumingAsync();
        Assert.False(bus.IsConsuming);
    }

    private static Bus BuildBus()
    {
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
            consumer.Object,
            producer: null);
    }
}
