using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), provider, new Lazy<IBus>(() => mockBus.Object));
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

        var processor = new HandlerProcessor(BuildRegistry(), provider, new Lazy<IBus>(() => mockBus.Object));
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

        var processor = new HandlerProcessor(BuildRegistry(), provider, new Lazy<IBus>(() => new Mock<IBus>().Object));
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

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), provider, new Lazy<IBus>(() => mockBus.Object));
        var msg = new TestHpMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        await processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), msg, headers, envelope);

        Assert.NotNull(handler.Context);
        Assert.Same(mockBus.Object, handler.Context.Bus);
        Assert.Same(headers, handler.Context.Headers);
    }

    [Fact]
    public async Task ProcessAsync_WithRoutingSlip_ForwardsToNextDestination()
    {
        var handler = new TestHpHandler();
        var mockBus = new Mock<IBus>();
        mockBus.Setup(b => b.RouteAsync(It.IsAny<TestHpMsg>(), It.IsAny<IList<string>>()))
            .Returns(Task.CompletedTask);
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handler);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), provider, new Lazy<IBus>(() => mockBus.Object));
        var msg = new TestHpMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object> { [HeaderKeys.RoutingSlip] = "Step2,Step3" };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        await processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), msg, headers, envelope);

        Assert.True(handler.Invoked);
        mockBus.Verify(b => b.RouteAsync(msg, It.Is<IList<string>>(d => d.Count == 2 && d[0] == "Step2" && d[1] == "Step3")), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WithRoutingSlipBytes_ForwardsToNextDestination()
    {
        var handler = new TestHpHandler();
        var mockBus = new Mock<IBus>();
        mockBus.Setup(b => b.RouteAsync(It.IsAny<TestHpMsg>(), It.IsAny<IList<string>>()))
            .Returns(Task.CompletedTask);
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<TestHpMsg>>(handler);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), provider, new Lazy<IBus>(() => mockBus.Object));
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

        var processor = new HandlerProcessor(BuildRegistry(typeof(TestHpMsg)), provider, new Lazy<IBus>(() => mockBus.Object));
        var msg = new TestHpMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object>();
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        await processor.ProcessAsync(new byte[] { 1 }, typeof(TestHpMsg), msg, headers, envelope);

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
