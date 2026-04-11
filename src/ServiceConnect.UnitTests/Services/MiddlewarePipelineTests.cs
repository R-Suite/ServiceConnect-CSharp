using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

#region Send middleware test types

file class RecordingSendMiddleware : ISendMessageMiddleware
{
    private readonly List<string> _log;

    public RecordingSendMiddleware(List<string> log)
    {
        _log = log;
    }

    public SendMessageDelegate Next { get; set; } = null!;

    public async Task Process(Type typeObject, byte[] messageBytes, Dictionary<string, string> headers, string? endPoint = null)
    {
        _log.Add("before");
        await Next(typeObject, messageBytes, headers, endPoint);
        _log.Add("after");
    }
}

file class ShortCircuitSendMiddleware : ISendMessageMiddleware
{
    public SendMessageDelegate Next { get; set; } = null!;

    public Task Process(Type typeObject, byte[] messageBytes, Dictionary<string, string> headers, string? endPoint = null)
    {
        // Intentionally does NOT call Next
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

    public MessageProcessingDelegate Next { get; set; } = null!;

    public async Task<ConsumeEventResult> Process(byte[] messageBytes, Type messageType, object message,
        IDictionary<string, object> headers, Envelope envelope)
    {
        _log.Add("before");
        var result = await Next(messageBytes, messageType, message, headers, envelope);
        _log.Add("after");
        return result;
    }
}

file class ShortCircuitProcessingMiddleware : IMessageProcessingMiddleware
{
    public MessageProcessingDelegate Next { get; set; } = null!;

    public Task<ConsumeEventResult> Process(byte[] messageBytes, Type messageType, object message,
        IDictionary<string, object> headers, Envelope envelope)
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
        _mockFilterPipeline.Setup(f => f.ExecuteBeforeConsumingFilters(It.IsAny<Envelope>())).Returns(false);
        _mockFilterPipeline.Setup(f => f.ExecuteAfterConsumingFilters(It.IsAny<Envelope>())).Returns(false);
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
                It.IsAny<IDictionary<string, object>>(), It.IsAny<Envelope>()))
            .Returns(() => { log.Add("processor"); return Task.FromResult(ProcessResult.Handled); });

        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<byte[]>(), typeof(string))).Returns("test");

        var dispatcher = new MessageDispatcher(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            new List<IMessageProcessor> { mockProcessor.Object },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MessageDispatcher>.Instance,
            new Mock<IBusConfiguration>().Object,
            mockPipelineConfig.Object,
            sp);

        var headers = MakeHeaders(typeof(string));

        // Act
        var result = await dispatcher.Dispatch(new byte[] { 1 }, "String", headers);

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

        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<byte[]>(), typeof(string))).Returns("test");

        var dispatcher = new MessageDispatcher(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            new List<IMessageProcessor> { mockProcessor.Object },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MessageDispatcher>.Instance,
            new Mock<IBusConfiguration>().Object,
            mockPipelineConfig.Object,
            sp);

        var headers = MakeHeaders(typeof(string));

        // Act
        var result = await dispatcher.Dispatch(new byte[] { 1 }, "String", headers);

        // Assert
        Assert.True(result.Success);
        mockProcessor.Verify(p => p.ProcessAsync(It.IsAny<byte[]>(), It.IsAny<Type>(), It.IsAny<object?>(),
            It.IsAny<IDictionary<string, object>>(), It.IsAny<Envelope>()), Times.Never);
    }
}
