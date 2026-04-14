using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class HandlerProcessorTests
{
    private static readonly IBusConfiguration DefaultBusConfig = new BusConfiguration();
    private static readonly IQueueConfiguration DefaultQueueConfig = new QueueConfiguration
    {
        QueueName = "test-queue",
        ErrorQueueName = "errors",
        AuditQueueName = "audit"
    };

    [Fact]
    public async Task ProcessAsync_WithRegisteredHandler_InvokesHandler()
    {
        var handler = new TestHpHandler();
        var mockBus = new Mock<IBus>();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handler);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), provider, new Lazy<IBus>(() => mockBus.Object), DefaultBusConfig, DefaultQueueConfig);
        var msg = new TestHpMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), msg, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
        Assert.True(handler.Invoked);
    }

    [Fact]
    public async Task ProcessAsync_NoHandlers_ReturnsNotHandled()
    {
        var mockBus = new Mock<IBus>();
        var services = new ServiceCollection();
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(BuildRegistry(), provider, new Lazy<IBus>(() => mockBus.Object), DefaultBusConfig, DefaultQueueConfig);
        var msg = new TestHpMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), msg, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_NullMessage_ReturnsNotHandled()
    {
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(BuildRegistry(), provider, new Lazy<IBus>(() => new Mock<IBus>().Object), DefaultBusConfig, DefaultQueueConfig);
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), null, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_SetsConsumeContext()
    {
        var handler = new TestHpHandler();
        var mockBus = new Mock<IBus>();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handler);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), provider, new Lazy<IBus>(() => mockBus.Object), DefaultBusConfig, DefaultQueueConfig);
        var msg = new TestHpMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        await processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), msg, headers, envelope);

        Assert.NotNull(handler.Context);
        Assert.Same(mockBus.Object, handler.Context.Bus);
        // Headers is wrapped in a ReadOnlyDictionary (R-088), so reference identity differs;
        // verify contents are equivalent instead.
        Assert.Equal(headers, handler.Context.Headers);
    }

    [Fact]
    public async Task ProcessAsync_WithRoutingSlip_ForwardsToKnownDestination()
    {
        var handler = new TestHpHandler();
        var mockBus = new Mock<IBus>();
        mockBus.Setup(b => b.RouteAsync(It.IsAny<TestHpMsg>(), It.IsAny<IList<string>>()))
            .Returns(Task.CompletedTask);
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handler);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        // Register the routing slip destinations as known queues
        var queueConfig = new QueueConfiguration { QueueName = "test-queue" };
        queueConfig.AddQueueMapping(typeof(TestHpMsg), "Step2");
        queueConfig.AddQueueMapping(typeof(TestHpMsg), "Step3");

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), provider, new Lazy<IBus>(() => mockBus.Object), DefaultBusConfig, queueConfig);
        var msg = new TestHpMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object> { [HeaderKeys.RoutingSlip] = "Step2,Step3" };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        await processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), msg, headers, envelope);

        Assert.True(handler.Invoked);
        mockBus.Verify(b => b.RouteAsync(msg, It.Is<IList<string>>(d => d.Count == 2 && d[0] == "Step2" && d[1] == "Step3")), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WithRoutingSlipBytes_ForwardsToKnownDestination()
    {
        var handler = new TestHpHandler();
        var mockBus = new Mock<IBus>();
        mockBus.Setup(b => b.RouteAsync(It.IsAny<TestHpMsg>(), It.IsAny<IList<string>>()))
            .Returns(Task.CompletedTask);
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handler);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        // Register the routing slip destination as a known queue
        var queueConfig = new QueueConfiguration { QueueName = "test-queue" };
        queueConfig.AddQueueMapping(typeof(TestHpMsg), "NextQueue");

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), provider, new Lazy<IBus>(() => mockBus.Object), DefaultBusConfig, queueConfig);
        var msg = new TestHpMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object> { [HeaderKeys.RoutingSlip] = System.Text.Encoding.UTF8.GetBytes("NextQueue") };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        await processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), msg, headers, envelope);

        mockBus.Verify(b => b.RouteAsync(msg, It.Is<IList<string>>(d => d.Count == 1 && d[0] == "NextQueue")), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_NoRoutingSlip_DoesNotCallRoute()
    {
        var handler = new TestHpHandler();
        var mockBus = new Mock<IBus>();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handler);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), provider, new Lazy<IBus>(() => mockBus.Object), DefaultBusConfig, DefaultQueueConfig);
        var msg = new TestHpMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        await processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), msg, headers, envelope);

        mockBus.Verify(b => b.RouteAsync(It.IsAny<TestHpMsg>(), It.IsAny<IList<string>>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_RoutingSlipToUnknownQueue_Throws()
    {
        var handler = new TestHpHandler();
        var mockBus = new Mock<IBus>();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handler);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), provider, new Lazy<IBus>(() => mockBus.Object), DefaultBusConfig, DefaultQueueConfig);
        var msg = new TestHpMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object> { [HeaderKeys.RoutingSlip] = "unknown-evil-queue" };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), msg, headers, envelope));
        Assert.Contains("not a recognized queue", ex.Message);
    }

    [Fact]
    public async Task ProcessAsync_RoutingSlipDisabled_SkipsProcessing()
    {
        var handler = new TestHpHandler();
        var mockBus = new Mock<IBus>();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handler);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var busConfig = new BusConfiguration { EnableRoutingSlipProcessing = false };
        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), provider, new Lazy<IBus>(() => mockBus.Object), busConfig, DefaultQueueConfig);
        var msg = new TestHpMsg(Guid.NewGuid());
        // This would normally throw because "SomeQueue" isn't known, but routing slip is disabled
        var headers = new Dictionary<string, object> { [HeaderKeys.RoutingSlip] = "SomeQueue" };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), msg, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
        Assert.True(handler.Invoked);
        mockBus.Verify(b => b.RouteAsync(It.IsAny<TestHpMsg>(), It.IsAny<IList<string>>()), Times.Never);
    }

    private static MessageHandlerRegistry BuildRegistry(params Type[] messageTypes)
    {
        var refs = messageTypes
            .Select(mt => new HandlerReference { MessageType = mt, HandlerType = typeof(TestHpHandler) })
            .ToList();
        return new MessageHandlerRegistry(
            refs,
            NullLogger<MessageHandlerRegistry>.Instance);
    }
}

file class TestHpMsg : Message
{
    public TestHpMsg(Guid correlationId) : base(correlationId) { }
}

file class TestHpHandler : IMessageHandler<TestHpMsg>
{
    public bool Invoked { get; private set; }
    public IConsumeContext? Context { get; set; }
    public Task HandleAsync(TestHpMsg message) { Invoked = true; return Task.CompletedTask; }
}
