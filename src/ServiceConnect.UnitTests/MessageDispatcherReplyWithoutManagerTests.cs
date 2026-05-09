using System.Text;
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

/// <summary>
/// Pins the contract that when a reply-shaped message (ResponseMessageId header
/// present) arrives at a bus with no ReplyProcessor / IRequestReplyManager registered,
/// the dispatcher must ack-and-drop rather than routing the payload to the regular
/// handler matching its CLR type. A regular handler running against a reply payload
/// would receive data correlated to a different request — a genuine correctness gap.
/// </summary>
public sealed class MessageDispatcherReplyWithoutManagerTests
{
    private readonly Mock<IFilterPipeline> _mockFilterPipeline = new();

    public MessageDispatcherReplyWithoutManagerTests()
    {
        _mockFilterPipeline
            .Setup(f => f.ExecuteBeforeConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Continue);
        _mockFilterPipeline
            .Setup(f => f.ExecuteAfterConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Continue);
        _mockFilterPipeline
            .Setup(f => f.ExecuteOnConsumedSuccessfullyFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Continue);
    }

    [Fact]
    public async Task DispatchAsync_ReplyHeaderPresentNoReplyProcessor_AcksAndDoesNotInvokeRegularHandler()
    {
        // Arrange — build a dispatcher whose processor list contains NO ReplyProcessor but
        // does contain a regular handler processor that would normally handle FakeMessage1.
        // The new guard must short-circuit before RunProcessors is reached.
        var handlerInvoked = false;
        var handlerProcessorMock = new Mock<IMessageProcessor>();
        handlerProcessorMock.SetupGet(p => p.RunBeforeDeserialization).Returns(false);
        handlerProcessorMock
            .Setup(p => p.ProcessAsync(
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<Type>(),
                It.IsAny<object?>(),
                It.IsAny<IDictionary<string, object>>(),
                It.IsAny<Envelope>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => handlerInvoked = true)
            .ReturnsAsync(ProcessResult.Handled);

        // FakeMessage1 is registered as a known type, simulating a CLR type that has a
        // regular handler registration. Without the fix this would cause the handler to run.
        var dispatcher = BuildDispatcher(processors: [handlerProcessorMock.Object]);

        var headers = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [HeaderKeys.FullTypeName] = Encoding.UTF8.GetBytes(typeof(FakeMessage1).AssemblyQualifiedName!),
            [HeaderKeys.ResponseMessageId] = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()),
        };

        // Act
        var result = await dispatcher.DispatchAsync(
            ReadOnlyMemory<byte>.Empty,
            typeof(FakeMessage1).AssemblyQualifiedName!,
            headers,
            CancellationToken.None);

        // Assert — ack-and-drop, no regular handler ran.
        Assert.True(result.Success);
        Assert.False(result.NotHandled);
        Assert.False(handlerInvoked, "Regular handler must NOT be invoked for a reply-shaped message when no ReplyProcessor is registered.");
    }

    [Fact]
    public async Task DispatchAsync_ReplyHeaderAbsent_NoReplyProcessor_DispatchesNormally()
    {
        // Sanity check: without reply headers the guard must not fire even when there is
        // no ReplyProcessor — the message should reach the regular handler.
        var handlerInvoked = false;
        var handlerProcessorMock = new Mock<IMessageProcessor>();
        handlerProcessorMock.SetupGet(p => p.RunBeforeDeserialization).Returns(false);
        handlerProcessorMock
            .Setup(p => p.ProcessAsync(
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<Type>(),
                It.IsAny<object?>(),
                It.IsAny<IDictionary<string, object>>(),
                It.IsAny<Envelope>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => handlerInvoked = true)
            .ReturnsAsync(ProcessResult.Handled);

        var mockSerializer = new Mock<IMessageSerializer>();
        mockSerializer
            .Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(new FakeMessage1(Guid.NewGuid()));

        var dispatcher = BuildDispatcher(processors: [handlerProcessorMock.Object], serializer: mockSerializer.Object);

        var headers = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [HeaderKeys.FullTypeName] = Encoding.UTF8.GetBytes(typeof(FakeMessage1).AssemblyQualifiedName!),
            // No ResponseMessageId header.
        };

        var result = await dispatcher.DispatchAsync(
            ReadOnlyMemory<byte>.Empty,
            typeof(FakeMessage1).AssemblyQualifiedName!,
            headers,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(handlerInvoked, "Regular handler must be invoked for a non-reply message.");
    }

    private MessageDispatcher BuildDispatcher(
        IEnumerable<IMessageProcessor> processors,
        IMessageSerializer? serializer = null)
    {
        var sp = new ServiceCollection().BuildServiceProvider();

        var registry = new MessageTypeRegistry();
        registry.Register(typeof(FakeMessage1));

        var pipelineConfig = new Mock<IPipelineConfiguration>();
        pipelineConfig.Setup(p => p.MessageProcessingMiddleware).Returns([]);

        // Deliberately exclude ReplyProcessor — this is the misconfiguration under test.
        var processorList = processors.ToList();

        return new MessageDispatcher(
            serializer ?? new Mock<IMessageSerializer>().Object,
            _mockFilterPipeline.Object,
            processorList,
            NullLogger<MessageDispatcher>.Instance,
            new Mock<IBusConfiguration>().Object,
            pipelineConfig.Object,
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ConsumeScopeAccessor(),
            registry);
    }
}
