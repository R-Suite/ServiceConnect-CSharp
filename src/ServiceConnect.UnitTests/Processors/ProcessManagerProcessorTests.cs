using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
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
        var processor = new ProcessManagerProcessor(registry, provider, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig);

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
        var processor = new ProcessManagerProcessor(registry, provider, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig);

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
        var processor = new ProcessManagerProcessor(registry, provider, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig);

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
        var processor = new ProcessManagerProcessor(registry, provider, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig);

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
        var processor = new ProcessManagerProcessor(registry, provider, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig);

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
        var processor = new ProcessManagerProcessor(registry, provider, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig);

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
        var processor = new ProcessManagerProcessor(registry, provider, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig);

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
        var processor = new ProcessManagerProcessor(registry, provider, new Lazy<IBus>(() => new Mock<IBus>().Object), NullLogger<ProcessManagerProcessor>.Instance, DefaultBusConfig, DefaultQueueConfig);

        var msg = new PmTestMessage(Guid.NewGuid()) { Content = "will-throw" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg,
                new Dictionary<string, object>(), new Envelope()));

        mockFinder.Verify(f => f.InsertDataAsync(
            It.IsAny<IProcessManagerData>(), It.IsAny<CancellationToken>()), Times.Never);
        mockFinder.Verify(f => f.UpdateDataAsync(
            It.IsAny<IPersistenceData<PmTestData>>(), It.IsAny<CancellationToken>()), Times.Never);
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
