using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

/// <summary>
/// Verifies that messages arriving with an unregistered type are routed as not-handled
/// rather than rejected with Success=false. Unregistered types are a terminal condition —
/// no amount of retrying will register the type — so burning the full retry budget through
/// nack/requeue is wasteful and risks filling the error queue with noise. The not-handled
/// path either dead-letters (when DeadLetterUnhandledMessages is enabled) or ack-and-drops,
/// which is the correct disposal strategy for a message the bus cannot process.
/// </summary>
public sealed class MessageDispatcherUnresolvedTypeTests
{
    private readonly Mock<IMessageSerializer> _mockSerializer = new();
    private readonly Mock<IFilterPipeline> _mockFilterPipeline = new();

    public MessageDispatcherUnresolvedTypeTests()
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
    public async Task DispatchAsync_UnresolvedType_NoResponseId_ReturnsNotHandled()
    {
        // Site 1: !typeResolvedFromRegistry && !hasResponseMessageId.
        // An unregistered type is terminal — the dispatcher must route as not-handled
        // instead of returning Success=false (which would drive nack/requeue → retry → DLQ burn).
        var dispatcher = BuildDispatcher(replyManager: null);

        var headers = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [HeaderKeys.FullTypeName] = System.Text.Encoding.UTF8.GetBytes("Foo.UnregisteredType"),
        };

        var result = await dispatcher.DispatchAsync(
            new ReadOnlyMemory<byte>([1, 2, 3]),
            "Foo.UnregisteredType",
            headers,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.NotHandled);
    }

    [Fact]
    public async Task DispatchAsync_UnresolvedType_WithResponseId_ButNoReplyProcessor_AcksAndDrops()
    {
        // Site 2: !typeResolvedFromRegistry, hasResponseMessageId=true, but no ReplyProcessor
        // in the processor list (replyProcessor is null). The reply-shape guard fires first
        // (replyProcessor == null && hasResponseMessageId) and ack-and-drops — the payload was
        // correlated to a request and must not be dispatched to a regular handler or treated as
        // a not-handled message. Success=true, NotHandled=false.
        var dispatcher = BuildDispatcher(replyManager: null, includeReplyProcessor: false);

        var headers = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [HeaderKeys.FullTypeName] = System.Text.Encoding.UTF8.GetBytes("Foo.UnregisteredType"),
            [HeaderKeys.ResponseMessageId] = System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()),
        };

        var result = await dispatcher.DispatchAsync(
            new ReadOnlyMemory<byte>([1, 2, 3]),
            "Foo.UnregisteredType",
            headers,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.NotHandled);
    }

    private MessageDispatcher BuildDispatcher(
        IReplyStatusRequestReplyManager? replyManager,
        bool includeReplyProcessor = true)
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var emptyRegistry = new MessageTypeRegistry(); // no types registered

        var pipelineConfig = new Mock<IPipelineConfiguration>();
        pipelineConfig.Setup(p => p.MessageProcessingMiddleware).Returns([]);

        var processors = new List<IMessageProcessor>();
        if (includeReplyProcessor && replyManager != null)
        {
            processors.Add(new ReplyProcessor(replyManager));
        }

        return new MessageDispatcher(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            processors,
            NullLogger<MessageDispatcher>.Instance,
            new Mock<IBusConfiguration>().Object,
            pipelineConfig.Object,
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ConsumeScopeAccessor(),
            emptyRegistry);
    }
}
