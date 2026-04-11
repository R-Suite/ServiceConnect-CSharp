using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

public class HandlerProcessorTests
{
    [Fact]
    public async Task ProcessAsync_WithRegisteredHandler_InvokesHandler()
    {
        var handler = new TestHpHandler();
        var mockBus = new Mock<IBus>();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handler);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(provider, new Mock<ILogger<HandlerProcessor>>().Object);
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

        var processor = new HandlerProcessor(provider, new Mock<ILogger<HandlerProcessor>>().Object);
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

        var processor = new HandlerProcessor(provider, new Mock<ILogger<HandlerProcessor>>().Object);
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

        var processor = new HandlerProcessor(provider, new Mock<ILogger<HandlerProcessor>>().Object);
        var msg = new TestHpMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        await processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), msg, headers, envelope);

        Assert.NotNull(handler.Context);
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
