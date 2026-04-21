using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Persistence.InMemory;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class ProcessManagerProcessorTests
{
    private static readonly IBusConfiguration DefaultBusConfig = new BusConfiguration();
    private static readonly IQueueConfiguration DefaultQueueConfig = new QueueConfiguration
    {
        QueueName = "test-queue",
        ErrorQueueName = "errors",
        AuditQueueName = "audit"
    };

    private static (ServiceCollection services, Mock<IBus> mockBus, Mock<IProcessManagerFinder> mockFinder) CreateBaseServices()
    {
        var services = new ServiceCollection();
        var mockBus = new Mock<IBus>();
        var mockFinder = new Mock<IProcessManagerFinder>();
        services.AddSingleton(mockBus.Object);
        services.AddSingleton<IProcessManagerFinder>(mockFinder.Object);
        return (services, mockBus, mockFinder);
    }

    private static ProcessManagerHandlerRegistry BuildRegistry(params HandlerReference[] refs)
        => new(refs.ToList(), NullLogger<ProcessManagerHandlerRegistry>.Instance);

    [Fact]
    public async Task ProcessAsync_NullMessage_ReturnsNotHandled()
    {
        var registry = BuildRegistry();
        var provider = new ServiceCollection().BuildServiceProvider();
        var processor = new ProcessManagerProcessor(registry, provider, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), null,
            new Dictionary<string, object>(), new Envelope());

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_NoDescriptor_ReturnsNotHandled()
    {
        var (services, _, _) = CreateBaseServices();
        var registry = BuildRegistry(); // empty
        var provider = services.BuildServiceProvider();
        var processor = new ProcessManagerProcessor(registry, provider, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        var msg = new PmTestMessage(Guid.NewGuid()) { Content = "x" };
        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg,
            new Dictionary<string, object>(), new Envelope());

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_NoFinder_ReturnsNotHandled()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IBus>().Object);
        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage), HandlerType = typeof(PmTestHandler)
        });
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(new PmTestHandler());
        var provider = services.BuildServiceProvider();
        var processor = new ProcessManagerProcessor(registry, provider, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        var msg = new PmTestMessage(Guid.NewGuid()) { Content = "x" };
        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg,
            new Dictionary<string, object>(), new Envelope());

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_NoHandlerInDi_ReturnsNotHandled()
    {
        var (services, _, _) = CreateBaseServices();
        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage), HandlerType = typeof(PmTestHandler)
        });
        var provider = services.BuildServiceProvider();
        var processor = new ProcessManagerProcessor(registry, provider, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        var msg = new PmTestMessage(Guid.NewGuid()) { Content = "x" };
        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg,
            new Dictionary<string, object>(), new Envelope());

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_NewData_InsertsAndReturnsHandled()
    {
        var (services, _, mockFinder) = CreateBaseServices();
        var handler = new PmTestHandler();
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(handler);

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage), HandlerType = typeof(PmTestHandler)
        });

        mockFinder.Setup(f => f.FindDataAsync<PmTestData>(
            It.IsAny<IProcessManagerPropertyMapper>(), It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IPersistenceData<PmTestData>?)null);

        var provider = services.BuildServiceProvider();
        var processor = new ProcessManagerProcessor(registry, provider, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        var correlationId = Guid.NewGuid();
        var msg = new PmTestMessage(correlationId) { Content = "test" };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg,
            new Dictionary<string, object>(), new Envelope());

        Assert.Equal(ProcessResult.Handled, result);
        Assert.True(handler.Invoked);
        mockFinder.Verify(f => f.InsertDataAsync(
            It.Is<IProcessManagerData>(d => d.CorrelationId == correlationId),
            It.IsAny<CancellationToken>()), Times.Once);
        mockFinder.Verify(f => f.UpdateDataAsync(
            It.IsAny<IPersistenceData<PmTestData>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_ExistingData_UpdatesAndReturnsHandled()
    {
        var (services, _, mockFinder) = CreateBaseServices();
        var handler = new PmTestHandler();
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(handler);

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage), HandlerType = typeof(PmTestHandler)
        });

        var existingData = new PmTestData { CorrelationId = Guid.NewGuid(), Counter = 5 };
        var persistence = new PmTestPersistenceData { Data = existingData };

        mockFinder.Setup(f => f.FindDataAsync<PmTestData>(
            It.IsAny<IProcessManagerPropertyMapper>(), It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(persistence);

        var provider = services.BuildServiceProvider();
        var processor = new ProcessManagerProcessor(registry, provider, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        var msg = new PmTestMessage(existingData.CorrelationId) { Content = "update" };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg,
            new Dictionary<string, object>(), new Envelope());

        Assert.Equal(ProcessResult.Handled, result);
        Assert.True(handler.Invoked);
        Assert.Equal(6, existingData.Counter);
        mockFinder.Verify(f => f.UpdateDataAsync(persistence, It.IsAny<CancellationToken>()), Times.Once);
        mockFinder.Verify(f => f.InsertDataAsync(It.IsAny<IProcessManagerData>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_CancelledToken_ThrowsOce()
    {
        var (services, _, _) = CreateBaseServices();
        var registry = BuildRegistry();
        var provider = services.BuildServiceProvider();
        var processor = new ProcessManagerProcessor(registry, provider, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), new PmTestMessage(Guid.NewGuid()),
                new Dictionary<string, object>(), new Envelope(), cts.Token));
    }

    [Fact]
    public async Task ProcessAsync_HandlerThrows_InsertDataAsyncNotCalled()
    {
        var (services, _, mockFinder) = CreateBaseServices();
        var handler = new PmThrowingHandler();
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(handler);

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage), HandlerType = typeof(PmThrowingHandler)
        });

        mockFinder.Setup(f => f.FindDataAsync<PmTestData>(
            It.IsAny<IProcessManagerPropertyMapper>(), It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IPersistenceData<PmTestData>?)null);

        var provider = services.BuildServiceProvider();
        var processor = new ProcessManagerProcessor(registry, provider, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        var msg = new PmTestMessage(Guid.NewGuid()) { Content = "will-throw" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg,
                new Dictionary<string, object>(), new Envelope()));

        mockFinder.Verify(f => f.InsertDataAsync(
            It.IsAny<IProcessManagerData>(), It.IsAny<CancellationToken>()), Times.Never);
        mockFinder.Verify(f => f.UpdateDataAsync(
            It.IsAny<IPersistenceData<PmTestData>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_WhenHandlerMutatesAndThrows_DoesNotLeakMutationIntoInMemoryStore()
    {
        var finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
        var existing = new PmMutableData { CorrelationId = Guid.NewGuid(), Counter = 5 };
        await finder.InsertDataAsync(existing, CancellationToken.None);

        var services = new ServiceCollection();
        services.AddSingleton<IBus>(new Mock<IBus>().Object);
        services.AddSingleton<IProcessManagerFinder>(finder);
        services.AddSingleton<IProcessHandler<PmMutableData, PmMutableMessage>>(new PmMutatingThrowingHandler());

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmMutableMessage), HandlerType = typeof(PmMutatingThrowingHandler)
        });
        var provider = services.BuildServiceProvider();
        var processor = new ProcessManagerProcessor(registry, provider, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), new ConsumeContextAccessor());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(new byte[] { 1 }, typeof(PmMutableMessage), new PmMutableMessage(existing.CorrelationId),
                new Dictionary<string, object>(), new Envelope()));

        var mapper = new TestProcessManagerPropertyMapper();
        mapper.ConfigureMapping<PmMutableData, PmMutableMessage>(d => d.CorrelationId, m => m.CorrelationId);
        var reloaded = await finder.FindDataAsync<PmMutableData>(mapper, new PmMutableMessage(existing.CorrelationId), CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.Equal(5, reloaded!.Data.Counter);
    }

    [Fact]
    public async Task ProcessAsync_SetsAmbientConsumeHeadersDuringHandlerAndClearsThemAfterward()
    {
        var timeoutStore = new PmCapturingTimeoutStore();
        var accessor = new ConsumeContextAccessor();
        var bus = PmTestBusFactory.Create(DefaultQueueConfig, timeoutStore, accessor);

        var services = new ServiceCollection();
        var mockFinder = new Mock<IProcessManagerFinder>();
        services.AddSingleton<IBus>(bus);
        services.AddSingleton<IProcessManagerFinder>(mockFinder.Object);
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(new PmTimeoutRequestingHandler(bus));

        var registry = BuildRegistry(new HandlerReference
        {
            MessageType = typeof(PmTestMessage), HandlerType = typeof(PmTimeoutRequestingHandler)
        });

        mockFinder.Setup(f => f.FindDataAsync<PmTestData>(
                It.IsAny<IProcessManagerPropertyMapper>(), It.IsAny<Message>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IPersistenceData<PmTestData>?)null);

        var provider = services.BuildServiceProvider();
        var processor = new ProcessManagerProcessor(registry, provider, new Lazy<IBus>(() => bus), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig, new ConsumeContextPool(), accessor);

        var correlationId = Guid.NewGuid();
        var headers = new Dictionary<string, object>
        {
            ["Custom"] = "value",
            [HeaderKeys.MessageId] = "managed-message-id"
        };

        await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), new PmTestMessage(correlationId), headers, new Envelope { Headers = headers, Body = new byte[] { 1 } });
        await bus.RequestTimeoutAsync(correlationId, TimeSpan.FromMinutes(2));

        Assert.Equal(2, timeoutStore.Inserted.Count);
        Assert.Equal("value", timeoutStore.Inserted[0].Headers["Custom"]);
        Assert.False(timeoutStore.Inserted[0].Headers.ContainsKey(HeaderKeys.MessageId));
        Assert.Empty(timeoutStore.Inserted[1].Headers);
    }
}

file class PmTestMessage : Message
{
    public PmTestMessage(Guid correlationId) : base(correlationId) { }
    public string Content { get; set; } = string.Empty;
}

file class PmTestData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public int Counter { get; set; }
}

file class PmTestPersistenceData : IPersistenceData<PmTestData>
{
    public PmTestData Data { get; set; } = new();
}

file class PmTestHandler : IProcessHandler<PmTestData, PmTestMessage>
{
    public bool Invoked { get; private set; }
    public IConsumeContext? Context { get; set; }

    public void ConfigureMapper(IProcessManagerPropertyMapper mapper) { }

    public Task HandleAsync(PmTestMessage message, PmTestData data)
    {
        data.Counter++;
        Invoked = true;
        return Task.CompletedTask;
    }
}

file class PmThrowingHandler : IProcessHandler<PmTestData, PmTestMessage>
{
    public IConsumeContext? Context { get; set; }

    public void ConfigureMapper(IProcessManagerPropertyMapper mapper) { }

    public Task HandleAsync(PmTestMessage message, PmTestData data)
        => throw new InvalidOperationException("handler failure");
}

file class PmMutableMessage : Message
{
    public PmMutableMessage(Guid correlationId) : base(correlationId) { }
}

file class PmMutableData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public int Counter { get; set; }
}

file class PmMutatingThrowingHandler : IProcessHandler<PmMutableData, PmMutableMessage>
{
    public IConsumeContext? Context { get; set; }

    public void ConfigureMapper(IProcessManagerPropertyMapper mapper)
        => mapper.ConfigureMapping<PmMutableData, PmMutableMessage>(d => d.CorrelationId, m => m.CorrelationId);

    public Task HandleAsync(PmMutableMessage message, PmMutableData data)
    {
        data.Counter++;
        throw new InvalidOperationException("handler failure");
    }
}

file sealed class PmTimeoutRequestingHandler(IBus bus) : IProcessHandler<PmTestData, PmTestMessage>
{
    public IConsumeContext? Context { get; set; }

    public void ConfigureMapper(IProcessManagerPropertyMapper mapper) { }

    public Task HandleAsync(PmTestMessage message, PmTestData data)
        => bus.RequestTimeoutAsync(message.CorrelationId, TimeSpan.FromMinutes(1));
}

file sealed class PmCapturingTimeoutStore : ITimeoutStore
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

    public Task RemoveDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task ReleaseDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

file static class PmTestBusFactory
{
    public static Bus Create(IQueueConfiguration queueConfiguration, ITimeoutStore timeoutStore, ConsumeContextAccessor accessor)
    {
        var serializer = new Mock<IMessageSerializer>();
        serializer.Setup(x => x.Serialize(It.IsAny<PmTestMessage>())).Returns([1]);

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
