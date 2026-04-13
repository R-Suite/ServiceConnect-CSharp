using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using Xunit;
using System.Text;

namespace ServiceConnect.UnitTests.Services;

file class TestMiddlewareMessage : Message
{
    public TestMiddlewareMessage() : base(Guid.NewGuid()) { }
}

#region Send middleware test types

file class RecordingSendMiddleware : ISendMessageMiddleware
{
    private readonly List<string> _log;

    public RecordingSendMiddleware(List<string> log)
    {
        _log = log;
    }

    public async Task Process(Type typeObject, byte[] messageBytes, Dictionary<string, string> headers, string? endPoint, SendMessageDelegate next, CancellationToken cancellationToken)
    {
        _log.Add("before");
        await next(typeObject, messageBytes, headers, endPoint, cancellationToken);
        _log.Add("after");
    }
}

file class ShortCircuitSendMiddleware : ISendMessageMiddleware
{
    public Task Process(Type typeObject, byte[] messageBytes, Dictionary<string, string> headers, string? endPoint, SendMessageDelegate next, CancellationToken cancellationToken)
    {
        // Intentionally does NOT call next
        return Task.CompletedTask;
    }
}

#endregion

#region Processing middleware test types

file class RecordingProcessingMiddleware : IMessageProcessingMiddleware
{
    private readonly List<string> _log;

    public RecordingProcessingMiddleware(List<string> log)
    {
        _log = log;
    }

    public async Task<ConsumeEventResult> Process(byte[] messageBytes, Type messageType, object message,
        IDictionary<string, object> headers, Envelope envelope, MessageProcessingDelegate next, CancellationToken cancellationToken)
    {
        _log.Add("before");
        var result = await next(messageBytes, messageType, message, headers, envelope, cancellationToken);
        _log.Add("after");
        return result;
    }
}

file class ShortCircuitProcessingMiddleware : IMessageProcessingMiddleware
{
    public Task<ConsumeEventResult> Process(byte[] messageBytes, Type messageType, object message,
        IDictionary<string, object> headers, Envelope envelope, MessageProcessingDelegate next, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ConsumeEventResult { Success = true });
    }
}

#endregion

public class SendMiddlewarePipelineTests
{
    private readonly Mock<IProducer> _mockProducer;

    public SendMiddlewarePipelineTests()
    {
        _mockProducer = new Mock<IProducer>();
        _mockProducer.Setup(p => p.PublishAsync(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(Task.CompletedTask);
        _mockProducer.Setup(p => p.SendAsync(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(Task.CompletedTask);
        _mockProducer.Setup(p => p.SendAsync(It.IsAny<string>(), It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task SendMiddleware_SingleMiddleware_WrapsProducerCall()
    {
        // Arrange
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddTransient<RecordingSendMiddleware>();
        var sp = services.BuildServiceProvider();

        var mockPipelineConfig = new Mock<IPipelineConfiguration>();
        mockPipelineConfig.Setup(p => p.SendMessageMiddleware)
            .Returns(new List<Type> { typeof(RecordingSendMiddleware) });

        _mockProducer.Setup(p => p.PublishAsync(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()))
            .Returns(() => { log.Add("producer"); return Task.CompletedTask; });

        var pipeline = new SendMessagePipeline(_mockProducer.Object, mockPipelineConfig.Object, sp);

        // Act
        await pipeline.ExecutePublishMessagePipelineAsync(typeof(string), new byte[] { 1 }, new Dictionary<string, string>());

        // Assert
        Assert.Equal(new[] { "before", "producer", "after" }, log);
    }

    [Fact]
    public async Task SendMiddleware_NoMiddleware_DirectProducerCall()
    {
        // Arrange
        var mockPipelineConfig = new Mock<IPipelineConfiguration>();
        mockPipelineConfig.Setup(p => p.SendMessageMiddleware).Returns(new List<Type>());
        var sp = new ServiceCollection().BuildServiceProvider();

        var pipeline = new SendMessagePipeline(_mockProducer.Object, mockPipelineConfig.Object, sp);

        // Act
        await pipeline.ExecutePublishMessagePipelineAsync(typeof(string), new byte[] { 1 }, new Dictionary<string, string>());

        // Assert
        _mockProducer.Verify(p => p.PublishAsync(typeof(string), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()), Times.Once);
    }

    [Fact]
    public async Task SendMiddleware_ShortCircuit_ProducerNotCalled()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddTransient<ShortCircuitSendMiddleware>();
        var sp = services.BuildServiceProvider();

        var mockPipelineConfig = new Mock<IPipelineConfiguration>();
        mockPipelineConfig.Setup(p => p.SendMessageMiddleware)
            .Returns(new List<Type> { typeof(ShortCircuitSendMiddleware) });

        var pipeline = new SendMessagePipeline(_mockProducer.Object, mockPipelineConfig.Object, sp);

        // Act
        await pipeline.ExecutePublishMessagePipelineAsync(typeof(string), new byte[] { 1 }, new Dictionary<string, string>());

        // Assert
        _mockProducer.Verify(p => p.PublishAsync(It.IsAny<Type>(), It.IsAny<byte[]>(), It.IsAny<Dictionary<string, string>>()), Times.Never);
    }
}

public class ProcessingMiddlewarePipelineTests
{
    private readonly Mock<IMessageSerializer> _mockSerializer;
    private readonly Mock<IFilterPipeline> _mockFilterPipeline;

    public ProcessingMiddlewarePipelineTests()
    {
        _mockSerializer = new Mock<IMessageSerializer>();
        _mockFilterPipeline = new Mock<IFilterPipeline>();
        _mockFilterPipeline.Setup(f => f.ExecuteBeforeConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _mockFilterPipeline.Setup(f => f.ExecuteAfterConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
    }

    private static IDictionary<string, object> MakeHeaders(Type messageType)
    {
        return new Dictionary<string, object>
        {
            [HeaderKeys.FullTypeName] = System.Text.Encoding.UTF8.GetBytes(messageType.AssemblyQualifiedName!)
        };
    }

    [Fact]
    public async Task ProcessingMiddleware_SingleMiddleware_WrapsProcessorCall()
    {
        // Arrange
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddTransient<RecordingProcessingMiddleware>();
        var sp = services.BuildServiceProvider();

        var mockPipelineConfig = new Mock<IPipelineConfiguration>();
        mockPipelineConfig.Setup(p => p.MessageProcessingMiddleware)
            .Returns(new List<Type> { typeof(RecordingProcessingMiddleware) });

        var mockProcessor = new Mock<IMessageProcessor>();
        mockProcessor.Setup(p => p.RunBeforeDeserialization).Returns(false);
        mockProcessor.Setup(p => p.ProcessAsync(It.IsAny<byte[]>(), It.IsAny<Type>(), It.IsAny<object?>(),
                It.IsAny<IDictionary<string, object>>(), It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .Returns(() => { log.Add("processor"); return Task.FromResult(ProcessResult.Handled); });

        var testMsg = new TestMiddlewareMessage();
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<byte[]>(), typeof(TestMiddlewareMessage))).Returns(testMsg);

        var registry = new MessageTypeRegistry();
        registry.Register(typeof(TestMiddlewareMessage));
        var dispatcher = new MessageDispatcher(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            new List<IMessageProcessor> { mockProcessor.Object },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MessageDispatcher>.Instance,
            new Mock<IBusConfiguration>().Object,
            mockPipelineConfig.Object,
            sp,
            registry);

        var headers = MakeHeaders(typeof(TestMiddlewareMessage));

        // Act
        var result = await dispatcher.Dispatch(new byte[] { 1 }, nameof(TestMiddlewareMessage), headers);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(new[] { "before", "processor", "after" }, log);
    }

    [Fact]
    public async Task ProcessingMiddleware_ShortCircuit_ProcessorNotCalled()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddTransient<ShortCircuitProcessingMiddleware>();
        var sp = services.BuildServiceProvider();

        var mockPipelineConfig = new Mock<IPipelineConfiguration>();
        mockPipelineConfig.Setup(p => p.MessageProcessingMiddleware)
            .Returns(new List<Type> { typeof(ShortCircuitProcessingMiddleware) });

        var mockProcessor = new Mock<IMessageProcessor>();
        mockProcessor.Setup(p => p.RunBeforeDeserialization).Returns(false);

        var testMsg = new TestMiddlewareMessage();
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<byte[]>(), typeof(TestMiddlewareMessage))).Returns(testMsg);

        var registry2 = new MessageTypeRegistry();
        registry2.Register(typeof(TestMiddlewareMessage));
        var dispatcher = new MessageDispatcher(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            new List<IMessageProcessor> { mockProcessor.Object },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MessageDispatcher>.Instance,
            new Mock<IBusConfiguration>().Object,
            mockPipelineConfig.Object,
            sp,
            registry2);

        var headers = MakeHeaders(typeof(TestMiddlewareMessage));

        // Act
        var result = await dispatcher.Dispatch(new byte[] { 1 }, nameof(TestMiddlewareMessage), headers);

        // Assert
        Assert.True(result.Success);
        mockProcessor.Verify(p => p.ProcessAsync(It.IsAny<byte[]>(), It.IsAny<Type>(), It.IsAny<object?>(),
            It.IsAny<IDictionary<string, object>>(), It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
