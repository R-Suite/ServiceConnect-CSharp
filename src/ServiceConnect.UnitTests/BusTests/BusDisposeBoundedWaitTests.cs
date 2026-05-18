using System;
using System.Collections.Generic;
using System.Diagnostics;
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

namespace ServiceConnect.UnitTests.BusTests;

/// <summary>
/// Pins the bounded-semaphore-wait fix in <see cref="Bus.DisposeAsync"/>: when the
/// lifecycle semaphore is held indefinitely (simulating a broker partition mid-handshake),
/// DisposeAsync must not block forever but must time out and proceed with teardown.
/// </summary>
public class BusDisposeBoundedWaitTests
{
    [Fact]
    public async Task DisposeAsync_LifecycleSemaphoreHeldIndefinitely_TimesOutAndProceeds()
    {
        // Build a bus with a very short DisposeTimeout so the test completes quickly.
        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(c => c.DisposeTimeout).Returns(TimeSpan.FromMilliseconds(200));

        var bus = BuildBus(busConfig.Object);

        // Acquire the lifecycle semaphore from outside (via reflection) and never release.
        // This simulates StartConsumingAsync wedged mid-handshake.
        var semaphoreField = typeof(Bus).GetField("_lifecycleSemaphore", BindingFlags.Instance | BindingFlags.NonPublic);
        var semaphore = (SemaphoreSlim)semaphoreField!.GetValue(bus)!;
        await semaphore.WaitAsync();

        try
        {
            var sw = Stopwatch.StartNew();
            await bus.DisposeAsync();
            sw.Stop();

            // The dispose must have timed out the semaphore wait (~200ms) and proceeded;
            // total elapsed should be close to 200ms, not indefinitely blocked.
            Assert.InRange(sw.Elapsed.TotalMilliseconds, 150, 5000);
        }
        finally
        {
            // Release the semaphore so the test cleans up without hanging.
            semaphore.Release();
        }
    }

    [Fact]
    public async Task DisposeAsync_SemaphoreUncontested_CompletesNormally()
    {
        // Happy path: semaphore is free, dispose should complete quickly.
        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(c => c.DisposeTimeout).Returns(TimeSpan.FromSeconds(30));

        var bus = BuildBus(busConfig.Object);

        var sw = Stopwatch.StartNew();
        await bus.DisposeAsync();
        sw.Stop();

        // An uncontested dispose with no consuming should complete well under 500ms.
        Assert.InRange(sw.Elapsed.TotalMilliseconds, 0, 2000);
    }

    [Fact]
    public async Task DisposeAsync_LogsWarningOnSemaphoreTimeout()
    {
        // Verify the warning log is emitted when the semaphore times out.
        var loggerMock = new Mock<ILogger<Bus>>();
        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(c => c.DisposeTimeout).Returns(TimeSpan.FromMilliseconds(100));

        var bus = BuildBus(busConfig.Object, loggerMock.Object);

        var semaphoreField = typeof(Bus).GetField("_lifecycleSemaphore", BindingFlags.Instance | BindingFlags.NonPublic);
        var semaphore = (SemaphoreSlim)semaphoreField!.GetValue(bus)!;
        await semaphore.WaitAsync();

        try
        {
            await bus.DisposeAsync();

            // A warning must have been logged for the timeout.
            loggerMock.Verify(
                l => l.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.IsAny<It.IsAnyType>(),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.AtLeastOnce);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static Bus BuildBus(IBusConfiguration busConfig, ILogger<Bus>? logger = null)
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
            consumer: null,
            busConfig: busConfig);
    }
}
