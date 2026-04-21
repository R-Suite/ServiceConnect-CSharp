using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class ExceptionHandlerTests
{
    private readonly Mock<IMessageSerializer> _mockSerializer;
    private readonly Mock<IFilterPipeline> _mockFilterPipeline;
    private readonly Mock<IBusConfiguration> _mockConfig;

    public ExceptionHandlerTests()
    {
        _mockSerializer = new Mock<IMessageSerializer>();
        _mockFilterPipeline = new Mock<IFilterPipeline>();
        _mockConfig = new Mock<IBusConfiguration>();

        // Default: filters don't block
        _mockFilterPipeline.Setup(f => f.ExecuteBeforeConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _mockFilterPipeline.Setup(f => f.ExecuteAfterConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
    }

    private static IDictionary<string, object> MakeHeaders()
    {
        return new Dictionary<string, object>
        {
            [HeaderKeys.FullTypeName] = Encoding.UTF8.GetBytes(typeof(FakeMessage1).AssemblyQualifiedName!)
        };
    }

    private MessageDispatcher CreateDispatcher(IList<IMessageProcessor> processors)
    {
        var mockPipelineConfig = new Mock<IPipelineConfiguration>();
        mockPipelineConfig.Setup(p => p.MessageProcessingMiddleware).Returns(new List<Type>());
        var sp = new ServiceCollection().BuildServiceProvider();
        var registry = new MessageTypeRegistry();
        registry.Register(typeof(FakeMessage1));
        return new MessageDispatcher(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            processors,
            NullLogger<MessageDispatcher>.Instance,
            _mockConfig.Object,
            mockPipelineConfig.Object,
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ConsumeScopeAccessor(),
            registry);
    }

    [Fact]
    public async Task Dispatch_HandlerThrows_ExceptionHandlerInvoked()
    {
        // Arrange
        var thrownException = new InvalidOperationException("Handler failure");
        Exception? capturedEx = null;
        _mockConfig.SetupProperty(c => c.ExceptionHandler, ex => capturedEx = ex);

        var mockProcessor = new Mock<IMessageProcessor>();
        mockProcessor.Setup(p => p.RunBeforeDeserialization).Returns(false);
        mockProcessor
            .Setup(p => p.ProcessAsync(
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<Type>(),
                It.IsAny<object?>(),
                It.IsAny<IDictionary<string, object>>(),
                It.IsAny<Envelope>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(thrownException);

        var message = new FakeMessage1(Guid.NewGuid());
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1))).Returns(message);

        var dispatcher = CreateDispatcher(new List<IMessageProcessor> { mockProcessor.Object });
        var headers = MakeHeaders();

        // Act
        var result = await dispatcher.Dispatch(new byte[] { 1, 2, 3 }, "FakeMessage1", headers);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(capturedEx);
        Assert.Same(thrownException, capturedEx);
    }

    [Fact]
    public async Task Dispatch_ExceptionHandlerIsNull_NoError()
    {
        // Arrange
        _mockConfig.SetupProperty(c => c.ExceptionHandler, null);

        var mockProcessor = new Mock<IMessageProcessor>();
        mockProcessor.Setup(p => p.RunBeforeDeserialization).Returns(false);
        mockProcessor
            .Setup(p => p.ProcessAsync(
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<Type>(),
                It.IsAny<object?>(),
                It.IsAny<IDictionary<string, object>>(),
                It.IsAny<Envelope>()))
            .ThrowsAsync(new InvalidOperationException("handler boom"));

        var message = new FakeMessage1(Guid.NewGuid());
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1))).Returns(message);

        var dispatcher = CreateDispatcher(new List<IMessageProcessor> { mockProcessor.Object });
        var headers = MakeHeaders();

        // Act
        var result = await dispatcher.Dispatch(new byte[] { 1, 2, 3 }, "FakeMessage1", headers);

        // Assert — no crash, result indicates failure
        Assert.False(result.Success);
        Assert.NotNull(result.Exception);
    }

    [Fact]
    public async Task Dispatch_ExceptionHandlerThrows_DoesNotBreakProcessing()
    {
        // Arrange — ExceptionHandler itself throws
        _mockConfig.SetupProperty(c => c.ExceptionHandler, _ => throw new Exception("handler itself exploded"));

        var mockProcessor = new Mock<IMessageProcessor>();
        mockProcessor.Setup(p => p.RunBeforeDeserialization).Returns(false);
        mockProcessor
            .Setup(p => p.ProcessAsync(
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<Type>(),
                It.IsAny<object?>(),
                It.IsAny<IDictionary<string, object>>(),
                It.IsAny<Envelope>()))
            .ThrowsAsync(new InvalidOperationException("original dispatch error"));

        var message = new FakeMessage1(Guid.NewGuid());
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1))).Returns(message);

        var dispatcher = CreateDispatcher(new List<IMessageProcessor> { mockProcessor.Object });
        var headers = MakeHeaders();

        // Act — should not throw even though ExceptionHandler throws
        var result = await dispatcher.Dispatch(new byte[] { 1, 2, 3 }, "FakeMessage1", headers);

        // Assert — still returns failure without crashing
        Assert.False(result.Success);
        Assert.NotNull(result.Exception);
    }
}
