using System.Collections.Generic;
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

/// <summary>
/// Pins the idempotent contract of <see cref="Bus.StopConsumingAsync"/>: calling it
/// after the bus has been disposed must not throw. A disposed bus is also a stopped
/// bus (DisposeAsync calls StopConsumingCoreAsync), so returning early on disposed
/// is semantically correct and avoids the noisy ObjectDisposedException that the host's
/// defensive StopAsync used to surface on every shutdown.
/// </summary>
public class BusStopConsumingIdempotenceTests
{
    [Fact]
    public async Task StopConsumingAsync_AfterDispose_DoesNotThrow()
    {
        var bus = BuildBus();
        await bus.DisposeAsync();

        // No throw — idempotent early-return on disposed.
        await bus.StopConsumingAsync();
    }

    [Fact]
    public async Task StopConsumingAsync_AfterDispose_CalledTwice_DoesNotThrow()
    {
        var bus = BuildBus();
        await bus.DisposeAsync();

        // Both calls must be safe.
        await bus.StopConsumingAsync();
        await bus.StopConsumingAsync();
    }

    [Fact]
    public async Task StopConsumingAsync_AfterDispose_DoesNotLogWarningOrError()
    {
        var loggerMock = new Mock<ILogger<Bus>>();
        var bus = BuildBus(loggerMock.Object);
        await bus.DisposeAsync();

        // Clear any dispose-path log calls so we only inspect the StopConsumingAsync call.
        loggerMock.Invocations.Clear();

        await bus.StopConsumingAsync();

        // The early-return path emits no log entries at all — certainly no warning/error.
        loggerMock.Verify(
            l => l.Log(
                It.Is<LogLevel>(ll => ll >= LogLevel.Warning),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    private static Bus BuildBus(ILogger<Bus>? logger = null)
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
            consumer: null);
    }
}
