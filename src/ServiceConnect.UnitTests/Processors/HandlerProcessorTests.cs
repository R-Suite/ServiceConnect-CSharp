using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect;
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
    public async Task ProcessAsync_PassesConsumeContextToHandler()
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

        Assert.True(handler.ContextWasReceived);
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
    public async Task ProcessAsync_RoutingSlipToQueueNotInLocalConfig_ForwardsSuccessfully()
    {
        // v8 removed the IsKnownQueue gate. A well-formed destination that is not
        // registered in queueConfig is now allowed — only format validation applies.
        var handler = new TestHpHandler();
        var mockBus = new Mock<IBus>();
        mockBus.Setup(b => b.RouteAsync(It.IsAny<TestHpMsg>(), It.IsAny<IList<string>>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handler);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), NewScope(provider), new Lazy<IBus>(() => mockBus.Object), DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());
        var msg = new TestHpMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object> { [HeaderKeys.RoutingSlip] = "unknown-cross-service-queue" };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), msg, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
        mockBus.Verify(
            b => b.RouteAsync(msg, It.Is<IList<string>>(d => d.Count == 1 && d[0] == "unknown-cross-service-queue"), It.IsAny<CancellationToken>()),
            Times.Once);
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

    [Fact]
    public async Task ProcessAsync_FirstHandlerThrows_RemainingHandlersStillRun()
    {
        // Handler A throws, B records, C throws — all three should run despite the faults.
        var handlerA = new ThrowingHpHandler("handler-A error");
        var handlerB = new RecordingHpHandler();
        var handlerC = new ThrowingHpHandler("handler-C error");

        var mockBus = new Mock<IBus>();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handlerA);
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handlerB);
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handlerC);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), NewScope(provider), new Lazy<IBus>(() => mockBus.Object), DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());
        var msg = new TestHpMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), msg, headers, envelope));

        // Both throwing handlers must have contributed their exception.
        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.Contains(ex.InnerExceptions, e => e.Message == "handler-A error");
        Assert.Contains(ex.InnerExceptions, e => e.Message == "handler-C error");

        // All three handlers must have been invoked — independent faults must not short-circuit.
        Assert.True(handlerA.Invoked);
        Assert.True(handlerB.Invoked);
        Assert.True(handlerC.Invoked);
    }

    [Fact]
    public async Task ProcessAsync_FirstHandlerThrowsOCE_RemainingHandlersSkipped()
    {
        // Handler A throws OperationCanceledException for the dispatch CT — shutdown path
        // must short-circuit cleanly and not invoke B or C.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var handlerA = new CancellingHpHandler(cts.Token);
        var handlerB = new RecordingHpHandler();
        var handlerC = new RecordingHpHandler();

        var mockBus = new Mock<IBus>();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handlerA);
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handlerB);
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handlerC);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), NewScope(provider), new Lazy<IBus>(() => mockBus.Object), DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());
        var msg = new TestHpMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        // ProcessAsync itself will throw before the loop because cancellationToken.ThrowIfCancellationRequested()
        // is called at entry. Use a fresh, already-cancelled token for the OCE inside the handler test.
        // Pass non-cancelled CT to the processor so it gets past the guard; the handler throws its own OCE
        // tied to cts.Token which is already cancelled.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), msg, headers, envelope, cts.Token));

        // Because cts.Token is cancelled, ProcessAsync throws at entry — handler A hasn't run yet.
        // This validates that a cancelled dispatch CT never reaches the loop.
        Assert.False(handlerA.Invoked);
        Assert.False(handlerB.Invoked);
        Assert.False(handlerC.Invoked);
    }

    [Fact]
    public async Task ProcessAsync_HandlerThrowsOCE_OnNonCancelledCT_IsTreatedAsFault()
    {
        // The dispatch CT is not cancelled, but handler A throws OCE bound to a
        // different (already-cancelled) token. Because the dispatch CT is not
        // cancelled, the in-loop `when` filter evaluates to false, so the OCE
        // falls through to the general catch and is aggregated. Handler B must
        // still run — independent faults must not short-circuit the loop.
        using var unrelatedCts = new CancellationTokenSource();
        await unrelatedCts.CancelAsync();

        var handlerA = new CancellingHpHandler(unrelatedCts.Token);
        var handlerB = new RecordingHpHandler();

        var mockBus = new Mock<IBus>();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handlerA);
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handlerB);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), NewScope(provider), new Lazy<IBus>(() => mockBus.Object), DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());
        var msg = new TestHpMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        using var dispatchCts = new CancellationTokenSource(); // intentionally not cancelled
        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), msg, headers, envelope, dispatchCts.Token));

        // The OCE from handler A must be captured as a handler fault.
        Assert.Single(ex.InnerExceptions);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerExceptions[0]);

        // Both handlers must have been invoked — the OCE is a fault, not a shutdown signal.
        Assert.True(handlerA.Invoked);
        Assert.True(handlerB.Invoked);
    }

    [Fact]
    public async Task ProcessAsync_PassesCancellationTokenToHandler()
    {
        var ctReceived = new TaskCompletionSource<CancellationToken>();
        var handler = new CtRecordingHandler(ctReceived);
        var mockBus = new Mock<IBus>();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<CtMsg>>(handler);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var refs = new List<HandlerReference> { new() { MessageType = typeof(CtMsg), HandlerType = typeof(CtRecordingHandler) } };
        var registry = new MessageHandlerRegistry(refs, NullLogger<MessageHandlerRegistry>.Instance);
        var processor = new HandlerProcessor(registry, NewScope(provider), new Lazy<IBus>(() => mockBus.Object), DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());
        var msg = new CtMsg();
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        using var cts = new CancellationTokenSource();
        await processor.ProcessAsync(new byte[] { 1 }, typeof(CtMsg), msg, headers, envelope, cts.Token);
        var observed = await ctReceived.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(cts.Token, observed);
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

file class TestHpMsg(Guid correlationId) : Message(correlationId)
{
}

// Throws a fixed exception message on every invocation — used to verify fault collection.
file sealed class ThrowingHpHandler(string errorMessage) : IMessageHandler<TestHpMsg>
{
    public bool Invoked { get; private set; }

    public Task HandleAsync(TestHpMsg message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        Invoked = true;
        throw new InvalidOperationException(errorMessage);
    }
}

// Records invocation without throwing — used to verify it still runs despite sibling faults.
file sealed class RecordingHpHandler : IMessageHandler<TestHpMsg>
{
    public bool Invoked { get; private set; }

    public Task HandleAsync(TestHpMsg message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        Invoked = true;
        return Task.CompletedTask;
    }
}

// Throws OperationCanceledException for the given token — used to exercise the OCE short-circuit path.
file sealed class CancellingHpHandler(CancellationToken token) : IMessageHandler<TestHpMsg>
{
    public bool Invoked { get; private set; }

    public Task HandleAsync(TestHpMsg message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        Invoked = true;
        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

file class TestHpHandler : IMessageHandler<TestHpMsg>
{
    public bool Invoked { get; private set; }
    // Capture context state during handler execution — the context parameter is only
    // valid for the duration of HandleAsync; capturing the reference itself is sufficient here.
    public IBus? ObservedBus { get; private set; }
    public IReadOnlyDictionary<string, object>? ObservedHeaders { get; private set; }
    public bool ContextWasReceived { get; private set; }

    public Task HandleAsync(TestHpMsg message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        Invoked = true;
        if (context != null)
        {
            ContextWasReceived = true;
            ObservedBus = context.Bus;
            ObservedHeaders = new Dictionary<string, object>(context.Headers);
        }
        return Task.CompletedTask;
    }
}

file sealed class TimeoutRequestingHandler(IBus bus) : IMessageHandler<TestHpMsg>
{
    public Task HandleAsync(TestHpMsg message, IConsumeContext context, CancellationToken cancellationToken = default)
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

    public Task<TimeoutsBatch> GetTimeoutsBatchAsync(int? batchSize = null, CancellationToken cancellationToken = default)
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
        pipelineConfiguration.Setup(x => x.OutgoingFilters).Returns([]);

        var rootProvider = new ServiceCollection().BuildServiceProvider();
        return new Bus(
            serializer.Object,
            filterPipeline.Object,
            sendPipeline.Object,
            requestReplyManager.Object,
            logger.Object,
            queueConfiguration,
            dispatcher.Object,
            [],
            pipelineConfiguration.Object,
            rootProvider.GetRequiredService<IServiceScopeFactory>(),
            new ConsumeScopeAccessor(),
            timeoutStore: timeoutStore,
            consumeContextAccessor: accessor);
    }
}

// Records the CancellationToken received by HandleAsync so the test can assert it
// is the same token that was passed into ProcessAsync.
file sealed class CtRecordingHandler(TaskCompletionSource<CancellationToken> tcs)
    : IMessageHandler<CtMsg>
{
    public Task HandleAsync(CtMsg message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        tcs.TrySetResult(cancellationToken);
        return Task.CompletedTask;
    }
}

file sealed class CtMsg : Message
{
    public CtMsg() : base(Guid.NewGuid()) { }
}
