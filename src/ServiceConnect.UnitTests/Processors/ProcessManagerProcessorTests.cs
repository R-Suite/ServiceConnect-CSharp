using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class ProcessManagerProcessorTests
{
    private static (ServiceCollection services, Mock<IBus> mockBus, Mock<IProcessManagerFinder> mockFinder) CreateBaseServices()
    {
        var services = new ServiceCollection();
        var mockBus = new Mock<IBus>();
        var mockFinder = new Mock<IProcessManagerFinder>();
        services.AddSingleton(mockBus.Object);
        services.AddSingleton<IProcessManagerFinder>(mockFinder.Object);
        return (services, mockBus, mockFinder);
    }

    [Fact]
    public async Task ProcessAsync_NoProcessHandler_ReturnsNotHandled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>());
        services.AddSingleton(new Mock<IBus>().Object);
        var provider = services.BuildServiceProvider();

        var processor = new ProcessManagerProcessor(provider, new Mock<ILogger<ProcessManagerProcessor>>().Object);
        var msg = new PmTestMessage(Guid.NewGuid()) { Content = "hello" };
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg, headers, envelope);

        Assert.Equal(ProcessResult.NotHandled, result);
    }

    [Fact]
    public async Task ProcessAsync_WithProcessHandler_NewState_InsertsData()
    {
        var (services, mockBus, mockFinder) = CreateBaseServices();

        var handler = new PmTestHandler();
        var handlerRefs = new List<HandlerReference>
        {
            new HandlerReference
            {
                MessageType = typeof(PmTestMessage),
                HandlerType = typeof(PmTestHandler),
                RoutingKeys = new List<string>()
            }
        };

        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(handler);

        // FindData returns null => new state
        mockFinder.Setup(f => f.FindData<PmTestData>(
                It.IsAny<IProcessManagerPropertyMapper>(),
                It.IsAny<Message>()))
            .Returns((IPersistenceData<PmTestData>)null!);

        var provider = services.BuildServiceProvider();
        var processor = new ProcessManagerProcessor(provider, new Mock<ILogger<ProcessManagerProcessor>>().Object);

        var correlationId = Guid.NewGuid();
        var msg = new PmTestMessage(correlationId) { Content = "test" };
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
        Assert.True(handler.Invoked);
        mockFinder.Verify(f => f.InsertData(It.Is<IProcessManagerData>(d => d.CorrelationId == correlationId)), Times.Once);
        mockFinder.Verify(f => f.UpdateData(It.IsAny<IPersistenceData<PmTestData>>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_WithProcessHandler_ExistingState_UpdatesData()
    {
        var (services, mockBus, mockFinder) = CreateBaseServices();

        var handler = new PmTestHandler();
        var handlerRefs = new List<HandlerReference>
        {
            new HandlerReference
            {
                MessageType = typeof(PmTestMessage),
                HandlerType = typeof(PmTestHandler),
                RoutingKeys = new List<string>()
            }
        };

        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton<IProcessHandler<PmTestData, PmTestMessage>>(handler);

        var existingData = new PmTestData { CorrelationId = Guid.NewGuid(), Counter = 5 };
        var persistenceData = new PmTestPersistenceData { Data = existingData };

        mockFinder.Setup(f => f.FindData<PmTestData>(
                It.IsAny<IProcessManagerPropertyMapper>(),
                It.IsAny<Message>()))
            .Returns(persistenceData);

        var provider = services.BuildServiceProvider();
        var processor = new ProcessManagerProcessor(provider, new Mock<ILogger<ProcessManagerProcessor>>().Object);

        var msg = new PmTestMessage(existingData.CorrelationId) { Content = "update" };
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(PmTestMessage), msg, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
        Assert.True(handler.Invoked);
        Assert.Equal(6, existingData.Counter);
        mockFinder.Verify(f => f.UpdateData(persistenceData), Times.Once);
        mockFinder.Verify(f => f.InsertData(It.IsAny<IProcessManagerData>()), Times.Never);
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

    public Task HandleAsync(PmTestMessage message, PmTestData data)
    {
        data.Counter++;
        Invoked = true;
        return Task.CompletedTask;
    }
}
