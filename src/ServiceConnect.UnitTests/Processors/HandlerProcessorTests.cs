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

    // Tests resolve handlers through a ConsumeScopeAccessor whose AsyncLocal is primed
    // with a per-test provider; each test class instance (xUnit creates one per fact)
    // runs in its own async flow, so the Push disposable can be discarded.
    private static ConsumeScopeAccessor NewScope(IServiceProvider sp)
    {
        var accessor = new ConsumeScopeAccessor();
        accessor.Push(sp);
        return accessor;
    }

    [Fact]
    public async Task ProcessAsync_WithRegisteredHandler_InvokesHandler()
    {
        var handler = new TestHpHandler();
        var mockBus = new Mock<IBus>();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handler);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), NewScope(provider), new Lazy<IBus>(() => mockBus.Object), DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());
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

        var processor = new HandlerProcessor(BuildRegistry(), NewScope(provider), new Lazy<IBus>(() => mockBus.Object), DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());
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

        var processor = new HandlerProcessor(BuildRegistry(), NewScope(provider), new Lazy<IBus>(() => new Mock<IBus>().Object), DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());
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

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), NewScope(provider), new Lazy<IBus>(() => mockBus.Object), DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());
        var msg = new TestHpMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        await processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), msg, headers, envelope);

        Assert.True(handler.ContextWasSet);
        Assert.Same(mockBus.Object, handler.ObservedBus);
        // Dictionary<string,object> implements IReadOnlyDictionary, so compare contents not reference.
        Assert.Equal(headers, handler.ObservedHeaders);
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

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), NewScope(provider), new Lazy<IBus>(() => mockBus.Object), DefaultBusConfig, queueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());
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

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), NewScope(provider), new Lazy<IBus>(() => mockBus.Object), DefaultBusConfig, queueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());
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

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), NewScope(provider), new Lazy<IBus>(() => mockBus.Object), DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());
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

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), NewScope(provider), new Lazy<IBus>(() => mockBus.Object), DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());
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
        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), NewScope(provider), new Lazy<IBus>(() => mockBus.Object), busConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());
        var msg = new TestHpMsg(Guid.NewGuid());
        // This would normally throw because "SomeQueue" isn't known, but routing slip is disabled
        var headers = new Dictionary<string, object> { [HeaderKeys.RoutingSlip] = "SomeQueue" };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), msg, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
        Assert.True(handler.Invoked);
        mockBus.Verify(b => b.RouteAsync(It.IsAny<TestHpMsg>(), It.IsAny<IList<string>>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_SetsAmbientConsumeHeadersDuringHandlerAndClearsThemAfterward()
    {
        var timeoutStore = new CapturingTimeoutStore();
        var accessor = new ConsumeContextAccessor();
        var bus = TestBusFactory.Create(DefaultQueueConfig, timeoutStore, accessor);
        var handler = new TimeoutRequestingHandler(bus);
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handler);
        services.AddSingleton<IBus>(bus);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), NewScope(provider), new Lazy<IBus>(() => bus), DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), accessor);
        var correlationId = Guid.NewGuid();
        var headers = new Dictionary<string, object>
        {
            ["Custom"] = "value",
            [HeaderKeys.MessageId] = "managed-message-id"
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        await processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), new TestHpMsg(correlationId), headers, envelope);
        await bus.RequestTimeoutAsync(correlationId, TimeSpan.FromMinutes(2));

        Assert.Equal(2, timeoutStore.Inserted.Count);
        Assert.Equal("value", timeoutStore.Inserted[0].Headers["Custom"]);
        Assert.False(timeoutStore.Inserted[0].Headers.ContainsKey(HeaderKeys.MessageId));
        Assert.Empty(timeoutStore.Inserted[1].Headers);
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
    public IConsumeContext Context { get; set; } = null!;
    // Capture context state during handler execution — Context is released afterwards
    // and raw property access would throw the escape-guard InvalidOperationException.
    public IBus? ObservedBus { get; private set; }
    public IReadOnlyDictionary<string, object>? ObservedHeaders { get; private set; }
    public bool ContextWasSet { get; private set; }

    public Task HandleAsync(TestHpMsg message)
    {
        Invoked = true;
        if (Context != null)
        {
            ContextWasSet = true;
            ObservedBus = Context.Bus;
            ObservedHeaders = new Dictionary<string, object>(Context.Headers);
        }
        return Task.CompletedTask;
    }
}

file sealed class TimeoutRequestingHandler(IBus bus) : IMessageHandler<TestHpMsg>
{
    public IConsumeContext Context { get; set; } = null!;

    public Task HandleAsync(TestHpMsg message)
        => bus.RequestTimeoutAsync(message.CorrelationId, TimeSpan.FromMinutes(1));
}

file sealed class CapturingTimeoutStore : ITimeoutStore
{
    public List<TimeoutData> Inserted { get; } = [];

    public Task InsertTimeoutAsync(TimeoutData data, CancellationToken cancellationToken = default)
    {
        Inserted.Add(new TimeoutData
        {
            Id = data.Id,
            Destination = data.Destination,
            ProcessManagerId = data.ProcessManagerId,
            Time = data.Time,
            Headers = new Dictionary<string, object>(data.Headers)
        });
        return Task.CompletedTask;
    }

    public Task<TimeoutsBatch> GetTimeoutsBatchAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task RemoveDispatchedTimeoutAsync(Guid id, Guid? lockOwner = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task ReleaseDispatchedTimeoutAsync(Guid id, Guid? lockOwner = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

file static class TestBusFactory
{
    public static Bus Create(IQueueConfiguration queueConfiguration, ITimeoutStore timeoutStore, ConsumeContextAccessor accessor)
    {
        var serializer = new Mock<IMessageSerializer>();
        serializer.Setup(x => x.Serialize(It.IsAny<TestHpMsg>())).Returns([1]);

        var filterPipeline = new Mock<IFilterPipeline>();
        var sendPipeline = new Mock<ISendMessagePipeline>();
        var requestReplyManager = new Mock<IRequestReplyManager>();
        var logger = new Mock<ILogger<Bus>>();
        var dispatcher = new Mock<IMessageDispatcher>();
        var pipelineConfiguration = new Mock<IPipelineConfiguration>();
        pipelineConfiguration.Setup(x => x.OutgoingFilters).Returns(new List<Type>());

        var rootProvider = new ServiceCollection().BuildServiceProvider();
        return new Bus(
            serializer.Object,
            filterPipeline.Object,
            sendPipeline.Object,
            requestReplyManager.Object,
            logger.Object,
            queueConfiguration,
            dispatcher.Object,
            new List<HandlerReference>(),
            pipelineConfiguration.Object,
            rootProvider.GetRequiredService<IServiceScopeFactory>(),
            new ConsumeScopeAccessor(),
            timeoutStore: timeoutStore,
            consumeContextAccessor: accessor);
    }
}
