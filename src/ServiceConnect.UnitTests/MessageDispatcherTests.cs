using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests;

file class TestDispatchHandler : IMessageHandler<FakeMessage1>
{
    public IConsumeContext? Context { get; set; }

    private readonly Action<FakeMessage1>? _onHandle;
    private readonly Action<IConsumeContext?>? _onContextSet;
    private readonly Exception? _throwOnHandle;

    public TestDispatchHandler(
        Action<FakeMessage1>? onHandle = null,
        Action<IConsumeContext?>? onContextSet = null,
        Exception? throwOnHandle = null)
    {
        _onHandle = onHandle;
        _onContextSet = onContextSet;
        _throwOnHandle = throwOnHandle;
    }

    public Task HandleAsync(FakeMessage1 message)
    {
        _onContextSet?.Invoke(Context);
        if (_throwOnHandle != null)
            throw _throwOnHandle;
        _onHandle?.Invoke(message);
        return Task.CompletedTask;
    }
}

public class MessageDispatcherTests
{
    private readonly Mock<IMessageSerializer> _mockSerializer;
    private readonly Mock<IFilterPipeline> _mockFilterPipeline;
    private readonly Mock<IRequestReplyManager> _mockReplyManager;
    private readonly Mock<IBus> _mockBus;

    private static IDictionary<string, object> MakeHeaders(string? responseMessageId = null)
    {
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.FullTypeName] = Encoding.UTF8.GetBytes(typeof(FakeMessage1).AssemblyQualifiedName!)
        };
        if (responseMessageId != null)
            headers["ResponseMessageId"] = Encoding.UTF8.GetBytes(responseMessageId);
        return headers;
    }

    public MessageDispatcherTests()
    {
        _mockSerializer = new Mock<IMessageSerializer>();
        _mockFilterPipeline = new Mock<IFilterPipeline>();
        _mockReplyManager = new Mock<IRequestReplyManager>();
        _mockBus = new Mock<IBus>();

        // Default: filters don't block
        _mockFilterPipeline.Setup(f => f.ExecuteBeforeConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _mockFilterPipeline.Setup(f => f.ExecuteAfterConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
    }

    private static Mock<IPipelineConfiguration> CreateEmptyPipelineConfig()
    {
        var mock = new Mock<IPipelineConfiguration>();
        mock.Setup(p => p.MessageProcessingMiddleware).Returns(new List<Type>());
        return mock;
    }

    private static MessageTypeRegistry CreateRegistryWithTypes(params Type[] types)
    {
        var registry = new MessageTypeRegistry();
        foreach (var t in types)
            registry.Register(t);
        return registry;
    }

    private static MessageHandlerRegistry BuildHandlerRegistry(params (Type MessageType, Type HandlerType)[] entries)
    {
        var refs = entries
            .Select(e => new HandlerReference { MessageType = e.MessageType, HandlerType = e.HandlerType })
            .ToList();
        return new MessageHandlerRegistry(refs, NullLogger<MessageHandlerRegistry>.Instance);
    }

    private MessageDispatcher CreateDispatcher(IServiceProvider serviceProvider)
    {
        var handlerRegistry = BuildHandlerRegistry(
            (typeof(FakeMessage1), typeof(TestDispatchHandler)),
            (typeof(PolyBaseMessage), typeof(PolyBaseHandler)));
        var processors = new List<IMessageProcessor>
        {
            new ReplyProcessor(_mockReplyManager.Object),
            new HandlerProcessor(handlerRegistry, serviceProvider, new Lazy<IBus>(() => serviceProvider.GetRequiredService<IBus>()), new BusConfiguration(), new QueueConfiguration())
        };

        var registry = CreateRegistryWithTypes(typeof(FakeMessage1), typeof(PolyBaseMessage), typeof(PolyDerivedMessage));

        return new MessageDispatcher(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            processors,
            NullLogger<MessageDispatcher>.Instance,
            new Mock<IBusConfiguration>().Object,
            CreateEmptyPipelineConfig().Object,
            serviceProvider,
            registry);
    }

    private MessageDispatcher CreateDispatcherWithProcessors(IList<IMessageProcessor> processors)
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var registry = CreateRegistryWithTypes(typeof(FakeMessage1));
        return new MessageDispatcher(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            processors,
            NullLogger<MessageDispatcher>.Instance,
            new Mock<IBusConfiguration>().Object,
            CreateEmptyPipelineConfig().Object,
            sp,
            registry);
    }

    [Fact]
    public async Task Dispatch_DeserializesAndCallsHandler()
    {
        // Arrange
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "TestUser" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<byte[]>(), typeof(FakeMessage1))).Returns(message);

        FakeMessage1? receivedMessage = null;
        var handler = new TestDispatchHandler(onHandle: m => receivedMessage = m);

        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<FakeMessage1>>(handler);
        services.AddSingleton(_mockBus.Object);
        var sp = services.BuildServiceProvider();

        var dispatcher = CreateDispatcher(sp);
        var headers = MakeHeaders();
        var messageBytes = new byte[] { 1, 2, 3 };

        // Act
        var result = await dispatcher.Dispatch(messageBytes, "FakeMessage1", headers);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(receivedMessage);
        Assert.Equal("TestUser", receivedMessage.Username);
        _mockSerializer.Verify(s => s.Deserialize(messageBytes, typeof(FakeMessage1)), Times.Once);
    }

    [Fact]
    public async Task Dispatch_SetsConsumeContextOnHandler()
    {
        // Arrange
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "ContextUser" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<byte[]>(), typeof(FakeMessage1))).Returns(message);

        IConsumeContext? capturedContext = null;
        var handler = new TestDispatchHandler(onContextSet: ctx => capturedContext = ctx);

        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<FakeMessage1>>(handler);
        services.AddSingleton(_mockBus.Object);
        var sp = services.BuildServiceProvider();

        var dispatcher = CreateDispatcher(sp);
        var headers = MakeHeaders();
        var messageBytes = new byte[] { 1, 2, 3 };

        // Act
        var result = await dispatcher.Dispatch(messageBytes, "FakeMessage1", headers);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(capturedContext);
        Assert.Same(headers, capturedContext.Headers);
    }

    [Fact]
    public async Task Dispatch_BeforeConsumingFilterBlocks_HandlerNotCalled()
    {
        // Arrange
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "BlockedUser" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<byte[]>(), typeof(FakeMessage1))).Returns(message);
        _mockFilterPipeline.Setup(f => f.ExecuteBeforeConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        bool handlerCalled = false;
        var handler = new TestDispatchHandler(onHandle: _ => handlerCalled = true);

        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<FakeMessage1>>(handler);
        services.AddSingleton(_mockBus.Object);
        var sp = services.BuildServiceProvider();

        var dispatcher = CreateDispatcher(sp);
        var headers = MakeHeaders();
        var messageBytes = new byte[] { 1, 2, 3 };

        // Act
        var result = await dispatcher.Dispatch(messageBytes, "FakeMessage1", headers);

        // Assert
        Assert.True(result.Success);
        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task Dispatch_ResponseMessage_RoutesToReplyManager()
    {
        // Arrange
        var replyId = Guid.NewGuid().ToString();
        var messageBytes = new byte[] { 1, 2, 3 };
        var headers = MakeHeaders(responseMessageId: replyId);

        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();

        var dispatcher = CreateDispatcher(sp);

        // Act
        var result = await dispatcher.Dispatch(messageBytes, "FakeMessage1", headers);

        // Assert
        Assert.True(result.Success);
        // Pre-deserialization processors receive typeof(Message) as a placeholder;
        // ReplyProcessor uses the expected reply type stored in RequestState, not this.
        _mockReplyManager.Verify(r => r.ProcessReply(replyId, It.IsAny<ReadOnlyMemory<byte>>(), typeof(Message)), Times.Once);
        _mockSerializer.Verify(s => s.Deserialize(It.IsAny<byte[]>(), It.IsAny<Type>()), Times.Never);
    }

    [Fact]
    public async Task Dispatch_HandlerThrows_ReturnsFailure()
    {
        // Arrange
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "ErrorUser" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<byte[]>(), typeof(FakeMessage1))).Returns(message);

        var thrownException = new InvalidOperationException("Handler failure");
        var handler = new TestDispatchHandler(throwOnHandle: thrownException);

        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<FakeMessage1>>(handler);
        services.AddSingleton(_mockBus.Object);
        var sp = services.BuildServiceProvider();

        var dispatcher = CreateDispatcher(sp);
        var headers = MakeHeaders();
        var messageBytes = new byte[] { 1, 2, 3 };

        // Act
        var result = await dispatcher.Dispatch(messageBytes, "FakeMessage1", headers);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Exception);
    }

    [Fact]
    public async Task Dispatch_NoHandler_ReturnsSuccess()
    {
        // Arrange
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "NoHandler" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<byte[]>(), typeof(FakeMessage1))).Returns(message);

        // No handlers registered — use empty service provider with HandlerProcessor
        var services = new ServiceCollection();
        services.AddSingleton(_mockBus.Object);
        var sp = services.BuildServiceProvider();

        var dispatcher = CreateDispatcher(sp);
        var headers = MakeHeaders();
        var messageBytes = new byte[] { 1, 2, 3 };

        // Act
        var result = await dispatcher.Dispatch(messageBytes, "FakeMessage1", headers);

        // Assert
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Dispatch_DerivedMessageType_InvokesBaseTypeHandler()
    {
        // Arrange
        var message = new PolyDerivedMessage(Guid.NewGuid()) { Content = "base", Extra = "derived" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<byte[]>(), typeof(PolyDerivedMessage))).Returns(message);

        var handler = new PolyBaseHandler();

        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<PolyBaseMessage>>(handler);
        services.AddSingleton(_mockBus.Object);
        var sp = services.BuildServiceProvider();

        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.FullTypeName] = Encoding.UTF8.GetBytes(typeof(PolyDerivedMessage).AssemblyQualifiedName!)
        };

        var dispatcher = CreateDispatcher(sp);
        var messageBytes = new byte[] { 1, 2, 3 };

        // Act
        var result = await dispatcher.Dispatch(messageBytes, "PolyDerivedMessage", headers);

        // Assert
        Assert.True(result.Success);
        Assert.True(handler.Invoked);
    }

    [Fact]
    public async Task Dispatch_UnregisteredType_ReturnsFailure()
    {
        // Arrange — empty registry, no types registered
        var emptyRegistry = new MessageTypeRegistry();
        var sp = new ServiceCollection().BuildServiceProvider();
        var processors = new List<IMessageProcessor>
        {
            new ReplyProcessor(_mockReplyManager.Object),
            new HandlerProcessor(BuildHandlerRegistry(), sp, new Lazy<IBus>(() => new Mock<IBus>().Object), new BusConfiguration(), new QueueConfiguration())
        };
        var dispatcher = new MessageDispatcher(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            processors,
            NullLogger<MessageDispatcher>.Instance,
            new Mock<IBusConfiguration>().Object,
            CreateEmptyPipelineConfig().Object,
            sp,
            emptyRegistry);

        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.FullTypeName] = Encoding.UTF8.GetBytes(typeof(FakeMessage1).AssemblyQualifiedName!)
        };

        // Act
        var result = await dispatcher.Dispatch(new byte[] { 1, 2, 3 }, "FakeMessage1", headers);

        // Assert
        Assert.False(result.Success);
    }
}

file class PolyBaseMessage : Message
{
    public PolyBaseMessage(Guid correlationId) : base(correlationId) { }
    public string Content { get; set; } = string.Empty;
}

file class PolyDerivedMessage : PolyBaseMessage
{
    public PolyDerivedMessage(Guid correlationId) : base(correlationId) { }
    public string Extra { get; set; } = string.Empty;
}

file class PolyBaseHandler : IMessageHandler<PolyBaseMessage>
{
    public bool Invoked { get; private set; }
    public IConsumeContext? Context { get; set; }
    public Task HandleAsync(PolyBaseMessage message) { Invoked = true; return Task.CompletedTask; }
}
