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
/// Pins the invariant that <see cref="Bus.IsConsuming"/> returns false once
/// <c>_disposed = 1</c> is set, even if <c>_consuming</c> is still true.
/// DisposeAsync sets <c>_disposed</c> BEFORE StopConsumingCoreAsync resets
/// <c>_consuming</c>; a health probe between those two writes must not report
/// Healthy on a bus already mid-teardown.
/// </summary>
public class BusIsConsumingDuringDisposeTests
{
    [Fact]
    public void IsConsuming_AfterDisposedFlagSet_ReturnsFalseEvenIfConsumingFlagStillTrue()
    {
        // Reflectively set _consuming = true and _disposed = 1; verify IsConsuming is false.
        var bus = BuildBus();
        var consumingField = typeof(Bus).GetField("_consuming", BindingFlags.Instance | BindingFlags.NonPublic);
        var disposedField = typeof(Bus).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic);

        consumingField!.SetValue(bus, true);
        disposedField!.SetValue(bus, 1);

        Assert.False(bus.IsConsuming);
    }

    [Fact]
    public void IsConsuming_NoDispose_ConsumingTrue_ReturnsTrue()
    {
        var bus = BuildBus();
        var consumingField = typeof(Bus).GetField("_consuming", BindingFlags.Instance | BindingFlags.NonPublic);
        consumingField!.SetValue(bus, true);

        // _disposed is 0 (default) and no consumer registered, so IsCancelledByBroker
        // evaluates as false via the null-coalescing path.
        Assert.True(bus.IsConsuming);
    }

    [Fact]
    public void IsConsuming_DisposedZero_ConsumingFalse_ReturnsFalse()
    {
        // Baseline: freshly constructed Bus is not consuming.
        var bus = BuildBus();
        Assert.False(bus.IsConsuming);
    }

    private static Bus BuildBus()
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
            consumer: null);
    }
}
