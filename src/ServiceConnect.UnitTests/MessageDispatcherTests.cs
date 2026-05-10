using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

file class TestDispatchHandler(
    Action<FakeMessage1>? onHandle = null,
    Action<IConsumeContext?>? onContextReceived = null,
    Exception? throwOnHandle = null) : IMessageHandler<FakeMessage1>
{
    private readonly Action<FakeMessage1>? _onHandle = onHandle;
    private readonly Action<IConsumeContext?>? _onContextReceived = onContextReceived;
    private readonly Exception? _throwOnHandle = throwOnHandle;

    public Task HandleAsync(FakeMessage1 message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        _onContextReceived?.Invoke(context);
        if (_throwOnHandle != null)
        {
            throw _throwOnHandle;
        }

        _onHandle?.Invoke(message);
        return Task.CompletedTask;
    }
}

public class MessageDispatcherTests
{
    private readonly Mock<IMessageSerializer> _mockSerializer;
    private readonly Mock<IFilterPipeline> _mockFilterPipeline;
    private readonly Mock<IBus> _mockBus;
    private readonly IReplyStatusRequestReplyManager _replyManager;

    private static Dictionary<string, object> MakeHeaders(string? responseMessageId = null)
    {
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.FullTypeName] = Encoding.UTF8.GetBytes(typeof(FakeMessage1).AssemblyQualifiedName!)
        };
        if (responseMessageId != null)
        {
            headers["ResponseMessageId"] = Encoding.UTF8.GetBytes(responseMessageId);
        }

        return headers;
    }

    public MessageDispatcherTests()
    {
        _mockSerializer = new Mock<IMessageSerializer>();
        _mockFilterPipeline = new Mock<IFilterPipeline>();
        _mockBus = new Mock<IBus>();
        _replyManager = new TestDispatcherReplyManager();

        // Default: filters don't block
        _mockFilterPipeline.Setup(f => f.ExecuteBeforeConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(FilterAction.Continue);
        _mockFilterPipeline.Setup(f => f.ExecuteAfterConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(FilterAction.Continue);
        _mockFilterPipeline.Setup(f => f.ExecuteOnConsumedSuccessfullyFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(FilterAction.Continue);
    }

    private static Mock<IPipelineConfiguration> CreateEmptyPipelineConfig()
    {
        var mock = new Mock<IPipelineConfiguration>();
        mock.Setup(p => p.MessageProcessingMiddleware).Returns([]);
        return mock;
    }

    private static MessageTypeRegistry CreateRegistryWithTypes(params Type[] types)
    {
        var registry = new MessageTypeRegistry();
        foreach (var t in types)
        {
            registry.Register(t);
        }

        return registry;
    }

    private static MessageHandlerRegistry BuildHandlerRegistry(params (Type MessageType, Type HandlerType)[] entries)
    {
        var refs = entries
            .Select(e => new HandlerReference { MessageType = e.MessageType, HandlerType = e.HandlerType })
            .ToList();
        return new MessageHandlerRegistry(refs, NullLogger<MessageHandlerRegistry>.Instance);
    }

    private MessageDispatcher CreateDispatcher(IServiceProvider serviceProvider, ILogger<MessageDispatcher>? logger = null)
    {
        var scopeAccessor = new ConsumeScopeAccessor();
        var handlerRegistry = BuildHandlerRegistry(
            (typeof(FakeMessage1), typeof(TestDispatchHandler)),
            (typeof(PolyBaseMessage), typeof(PolyBaseHandler)));
        var processors = new List<IMessageProcessor>
        {
            new ReplyProcessor(_replyManager),
            new HandlerProcessor(handlerRegistry, scopeAccessor, new Lazy<IBus>(serviceProvider.GetRequiredService<IBus>), new BusConfiguration(), new QueueConfiguration(), new ConsumeContextPool(), new ConsumeContextAccessor(), Microsoft.Extensions.Logging.Abstractions.NullLogger<HandlerProcessor>.Instance)
        };

        var registry = CreateRegistryWithTypes(typeof(FakeMessage1), typeof(PolyBaseMessage), typeof(PolyDerivedMessage));

        return new MessageDispatcher(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            processors,
            logger ?? NullLogger<MessageDispatcher>.Instance,
            new Mock<IBusConfiguration>().Object,
            CreateEmptyPipelineConfig().Object,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            scopeAccessor,
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
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ConsumeScopeAccessor(),
            registry);
    }

    [Fact]
    public async Task Dispatch_DeserializesAndCallsHandler()
    {
        // Arrange
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "TestUser" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1))).Returns(message);

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
        var result = await dispatcher.DispatchAsync(messageBytes, "FakeMessage1", headers);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(receivedMessage);
        Assert.Equal("TestUser", receivedMessage.Username);
        _mockSerializer.Verify(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_OnHandlerSuccess_InvokesOnConsumedSuccessfullyFilters()
    {
        // Arrange — copied verbatim from Dispatch_DeserializesAndCallsHandler
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "TestUser" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1))).Returns(message);

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
        var result = await dispatcher.DispatchAsync(messageBytes, "FakeMessage1", headers);

        // Assert
        Assert.True(result.Success);
        Assert.False(result.NotHandled);
        _mockFilterPipeline.Verify(
            f => f.ExecuteOnConsumedSuccessfullyFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_OnHandlerThrow_DoesNotInvokeOnConsumedSuccessfullyFilters()
    {
        // Arrange — copied from Dispatch_HandlerThrows_ReturnsFailure
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "ErrorUser" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1))).Returns(message);

        var thrown = new InvalidOperationException("handler boom");
        var handler = new TestDispatchHandler(throwOnHandle: thrown);

        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<FakeMessage1>>(handler);
        services.AddSingleton(_mockBus.Object);
        var sp = services.BuildServiceProvider();

        var dispatcher = CreateDispatcher(sp);
        var headers = MakeHeaders();
        var messageBytes = new byte[] { 1, 2, 3 };

        // Act
        var result = await dispatcher.DispatchAsync(messageBytes, "FakeMessage1", headers);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Exception);
        // HandlerProcessor wraps handler exceptions in AggregateException; the original is an inner exception.
        var aggregate = Assert.IsType<AggregateException>(result.Exception);
        Assert.Same(thrown, aggregate.InnerException);
        _mockFilterPipeline.Verify(
            f => f.ExecuteOnConsumedSuccessfullyFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // The existing finally-block behaviour is unchanged: AfterConsumingFilters still runs.
        _mockFilterPipeline.Verify(
            f => f.ExecuteAfterConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WhenNotHandled_DoesNotInvokeOnConsumedSuccessfullyFilters()
    {
        // Arrange — copied from Dispatch_NoProcessorHandlesMessage_ReturnsNotHandled
        // Empty processor list → NotHandled=true. The on-success stage must NOT be invoked
        // even though Success=true (NotHandled=true acks-and-drops without recording).
        _mockSerializer
            .Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(new FakeMessage1(Guid.NewGuid()));

        var dispatcher = CreateDispatcherWithProcessors([]);
        var headers = MakeHeaders();

        // Act
        var result = await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, "FakeMessage1", headers);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.NotHandled);
        _mockFilterPipeline.Verify(
            f => f.ExecuteOnConsumedSuccessfullyFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
        // Existing finally-block behaviour unchanged: AfterConsumingFilters still runs.
        _mockFilterPipeline.Verify(
            f => f.ExecuteAfterConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_OnSuccessFilterThrows_PropagatesAsFailure()
    {
        // Arrange — copied from DispatchAsync_OnHandlerSuccess_InvokesOnConsumedSuccessfullyFilters,
        // but ExecuteOnConsumedSuccessfullyFiltersAsync is overridden to throw.
        // The dispatcher's existing catch block turns this into Success=false. AfterConsumingFilters
        // in the finally block must still run (existing behaviour unchanged).
        var thrown = new InvalidOperationException("on-success boom");

        _mockFilterPipeline
            .Setup(f => f.ExecuteOnConsumedSuccessfullyFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(thrown);

        var message = new FakeMessage1(Guid.NewGuid()) { Username = "TestUser" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1))).Returns(message);

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
        var result = await dispatcher.DispatchAsync(messageBytes, "FakeMessage1", headers);

        // Assert
        Assert.False(result.Success);
        Assert.Same(thrown, result.Exception);
        _mockFilterPipeline.Verify(
            f => f.ExecuteAfterConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Dispatch_PassesConsumeContextToHandler()
    {
        // Arrange
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "ContextUser" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1))).Returns(message);

        // Capture properties during handler invocation — IConsumeContext becomes invalid
        // after the handler returns (pool token check). We can't dereference it post-dispatch.
        bool contextWasReceived = false;
        IReadOnlyDictionary<string, object>? capturedHeaders = null;
        var handler = new TestDispatchHandler(onContextReceived: ctx =>
        {
            contextWasReceived = ctx != null;
            capturedHeaders = ctx?.Headers == null ? null : new Dictionary<string, object>(ctx.Headers);
        });

        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<FakeMessage1>>(handler);
        services.AddSingleton(_mockBus.Object);
        var sp = services.BuildServiceProvider();

        var dispatcher = CreateDispatcher(sp);
        var headers = MakeHeaders();
        var messageBytes = new byte[] { 1, 2, 3 };

        // Act
        var result = await dispatcher.DispatchAsync(messageBytes, "FakeMessage1", headers);

        // Assert
        Assert.True(result.Success);
        Assert.True(contextWasReceived);
        Assert.NotNull(capturedHeaders);
        Assert.Equal(headers, capturedHeaders);
    }

    [Fact]
    public async Task Dispatch_BeforeConsumingFilterBlocks_HandlerNotCalled()
    {
        // Arrange
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "BlockedUser" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1))).Returns(message);
        _mockFilterPipeline.Setup(f => f.ExecuteBeforeConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(FilterAction.Stop);

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
        var result = await dispatcher.DispatchAsync(messageBytes, "FakeMessage1", headers);

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
        var result = await dispatcher.DispatchAsync(messageBytes, "FakeMessage1", headers);

        // Assert
        Assert.True(result.Success);
        var replyManager = Assert.IsType<TestDispatcherReplyManager>(_replyManager);
        Assert.Equal(replyId, replyManager.LastMessageId);
        Assert.Equal(typeof(FakeMessage1), replyManager.LastMessageType);
        _mockSerializer.Verify(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<Type>()), Times.Never);
        _mockFilterPipeline.Verify(f => f.ExecuteAfterConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dispatch_ResponseMessage_Handled_InvokesOnConsumedSuccessfullyFilters()
    {
        // The reply-handled branch must invoke OnConsumedSuccessfully filters so audit and
        // telemetry filters that count successful consumes see reply messages too — the
        // non-reply success path already does this.
        var replyId = Guid.NewGuid().ToString();
        var headers = MakeHeaders(responseMessageId: replyId);

        var dispatcher = CreateDispatcher(new ServiceCollection().BuildServiceProvider());

        var result = await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, "FakeMessage1", headers);

        Assert.True(result.Success);
        Assert.Equal(replyId, Assert.IsType<TestDispatcherReplyManager>(_replyManager).LastMessageId);
        _mockFilterPipeline.Verify(
            f => f.ExecuteOnConsumedSuccessfullyFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Dispatch_ResponseMessage_UntrackedReply_StillInvokesOnConsumedSuccessfullyFilters()
    {
        // The reply-discarded branch (no pending request matched) must still invoke the
        // success-filter pipeline: the dispatcher acks the broker, so by the user-facing
        // contract the message was successfully consumed.
        var replyId = Guid.NewGuid().ToString();
        var headers = MakeHeaders(responseMessageId: replyId);
        Assert.IsType<TestDispatcherReplyManager>(_replyManager).ShouldHandleReplies = false;

        var dispatcher = CreateDispatcher(new ServiceCollection().BuildServiceProvider());

        var result = await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, "FakeMessage1", headers);

        Assert.True(result.Success);
        _mockFilterPipeline.Verify(
            f => f.ExecuteOnConsumedSuccessfullyFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Dispatch_ResponseMessage_BlockedByBeforeConsumingFilter_DoesNotReachReplyManager()
    {
        var replyId = Guid.NewGuid().ToString();
        var headers = MakeHeaders(responseMessageId: replyId);
        _mockFilterPipeline
            .Setup(f => f.ExecuteBeforeConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Stop);
        _mockSerializer
            .Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(new FakeMessage1(Guid.NewGuid()));

        var dispatcher = CreateDispatcher(new ServiceCollection().BuildServiceProvider());

        var result = await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, "FakeMessage1", headers);

        Assert.True(result.Success);
        Assert.Equal(0, Assert.IsType<TestDispatcherReplyManager>(_replyManager).CallCount);
    }

    // ---------------- Pre-deserialization processor filter coverage ----------------

    [Fact]
    public async Task Dispatch_PreDeserProcessor_RunsAfterBeforeFilter()
    {
        // Before-filters must run before pre-deserialization processors so nothing
        // — including StreamProcessor-style pre-deser handling — can bypass the
        // filter gate.
        var order = new List<string>();
        _mockFilterPipeline
            .Setup(f => f.ExecuteBeforeConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("before-filter"))
            .ReturnsAsync(FilterAction.Continue);

        var preDeser = new OrderRecordingPreDeserProcessor(order);
        var dispatcher = CreateDispatcherWithProcessors([preDeser]);

        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(new FakeMessage1(Guid.NewGuid()));

        await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, "FakeMessage1", MakeHeaders());

        Assert.Equal("before-filter", order[0]);
        Assert.Equal("pre-deser-processor", order[1]);
    }

    [Fact]
    public async Task Dispatch_PreDeserProcessor_BlockedByBeforeFilter_DoesNotRun()
    {
        // A blocking before-filter must prevent pre-deserialization processors
        // from running at all, so no processor can slip past the filter gate
        // and observe or handle a message the filter rejected.
        _mockFilterPipeline
            .Setup(f => f.ExecuteBeforeConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Stop);

        var preDeser = new OrderRecordingPreDeserProcessor([]);
        var dispatcher = CreateDispatcherWithProcessors([preDeser]);

        var result = await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, "FakeMessage1", MakeHeaders());

        Assert.True(result.Success);
        Assert.Equal(0, preDeser.CallCount);
    }

    [Fact]
    public async Task Dispatch_PreDeserProcessor_Handled_StillRunsAfterFilter()
    {
        // When a pre-deser processor reports Handled (e.g., stream packet accepted),
        // after-consuming filters must still fire — they were being skipped when
        // the processor returned before the before-filter step.
        var preDeser = new OrderRecordingPreDeserProcessor([]) { ReturnHandled = true };
        var dispatcher = CreateDispatcherWithProcessors([preDeser]);

        var result = await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, "FakeMessage1", MakeHeaders());

        Assert.True(result.Success);
        _mockFilterPipeline.Verify(
            f => f.ExecuteAfterConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Dispatch_PreDeserProcessor_Handled_SkipsDeserialization()
    {
        // Pre-deser Handled return short-circuits dispatch before deserialization,
        // keeping the middleware asymmetry (middleware requires a deserialized message).
        var preDeser = new OrderRecordingPreDeserProcessor([]) { ReturnHandled = true };
        var dispatcher = CreateDispatcherWithProcessors([preDeser]);

        await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, "FakeMessage1", MakeHeaders());

        _mockSerializer.Verify(
            s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<Type>()),
            Times.Never);
    }

    [Fact]
    public async Task Dispatch_ResponseMessage_WithUnknownReplyId_ReturnsSuccess()
    {
        // Untracked replies return Success=true so the dispatcher silently acks stale or
        // duplicate replies and avoids spurious retry/DLQ churn. After-consuming filters
        // must still run on the message.
        var replyId = Guid.NewGuid().ToString();
        var headers = MakeHeaders(responseMessageId: replyId);
        Assert.IsType<TestDispatcherReplyManager>(_replyManager).ShouldHandleReplies = false;
        _mockSerializer
            .Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(new FakeMessage1(Guid.NewGuid()));

        var dispatcher = CreateDispatcher(new ServiceCollection().BuildServiceProvider());

        var result = await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, "FakeMessage1", headers);

        Assert.True(result.Success);
        _mockFilterPipeline.Verify(f => f.ExecuteAfterConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dispatch_ResponseMessage_WithUnregisteredReplyType_RoutesToReplyManagerAsMessage()
    {
        // Unregistered but loadable type: reply traffic must resolve to typeof(Message), not the wire type.
        var replyId = Guid.NewGuid().ToString();
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.FullTypeName] = Encoding.UTF8.GetBytes(typeof(UnregisteredReplyMessage).AssemblyQualifiedName!),
            [HeaderKeys.ResponseMessageId] = Encoding.UTF8.GetBytes(replyId)
        };

        var dispatcher = CreateDispatcher(new ServiceCollection().BuildServiceProvider());

        var result = await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, nameof(UnregisteredReplyMessage), headers);

        Assert.True(result.Success);
        var replyManager = Assert.IsType<TestDispatcherReplyManager>(_replyManager);
        Assert.Equal(replyId, replyManager.LastMessageId);
        Assert.Equal(typeof(Message), replyManager.LastMessageType);
        _mockSerializer.Verify(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<Type>()), Times.Never);
    }

    [Fact]
    public async Task Dispatch_UntrackedReply_ReturnsSuccess_NotError()
    {
        // A reply that arrives after the caller has timed out (or is a duplicate)
        // must be silently discarded — returning Success=false would drive nack/requeue
        // and cause spurious retry/DLQ churn. The Debug log must carry the correlation id
        // so operators can diagnose which request timed out.
        var replyId = Guid.NewGuid().ToString();
        var headers = MakeHeaders(responseMessageId: replyId);
        Assert.IsType<TestDispatcherReplyManager>(_replyManager).ShouldHandleReplies = false;
        var mockLogger = new Mock<ILogger<MessageDispatcher>>();

        var dispatcher = CreateDispatcher(new ServiceCollection().BuildServiceProvider(), mockLogger.Object);

        var result = await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, "FakeMessage1", headers);

        Assert.True(result.Success);
        Assert.Null(result.Exception);
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(replyId)),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Dispatch_ResponseMessage_WithUnloadableReplyType_RoutesToReplyManager()
    {
        var replyId = Guid.NewGuid().ToString();
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.FullTypeName] = Encoding.UTF8.GetBytes("Missing.Namespace.MissingReply, Missing.Assembly"),
            [HeaderKeys.ResponseMessageId] = Encoding.UTF8.GetBytes(replyId)
        };

        var dispatcher = CreateDispatcher(new ServiceCollection().BuildServiceProvider());

        var result = await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, "MissingReply", headers);

        Assert.True(result.Success);
        var replyManager = Assert.IsType<TestDispatcherReplyManager>(_replyManager);
        Assert.Equal(replyId, replyManager.LastMessageId);
        Assert.Equal(typeof(Message), replyManager.LastMessageType);
        _mockSerializer.Verify(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<Type>()), Times.Never);
    }

    [Fact]
    public async Task Dispatch_HandlerThrows_ReturnsFailure()
    {
        // Arrange
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "ErrorUser" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1))).Returns(message);

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
        var result = await dispatcher.DispatchAsync(messageBytes, "FakeMessage1", headers);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.Exception);
    }

    [Fact]
    public async Task Dispatch_NoHandler_ReturnsSuccess()
    {
        // Arrange
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "NoHandler" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1))).Returns(message);

        // No handlers registered — use empty service provider with HandlerProcessor
        var services = new ServiceCollection();
        services.AddSingleton(_mockBus.Object);
        var sp = services.BuildServiceProvider();

        var dispatcher = CreateDispatcher(sp);
        var headers = MakeHeaders();
        var messageBytes = new byte[] { 1, 2, 3 };

        // Act
        var result = await dispatcher.DispatchAsync(messageBytes, "FakeMessage1", headers);

        // Assert
        Assert.True(result.Success);
    }

    [Fact]
    public async Task DispatchAsync_HandlerThrowsOceDuringShutdown_PropagatesOce_AndDoesNotInvokeExceptionHandler()
    {
        // Arrange — same shape as Dispatch_HandlerThrows_ReturnsFailure, but the cancellation token
        // is pre-cancelled to signal cooperative shutdown. The dispatcher must propagate the OCE
        // rather than catching it and returning Success=false.
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "ShutdownUser" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1))).Returns(message);

        // The handler itself is never reached because HandlerProcessor.ThrowIfCancellationRequested
        // fires first on a pre-cancelled token — the important property is that the OCE escapes the
        // dispatcher catch block rather than being turned into Success=false.
        var handler = new TestDispatchHandler(onHandle: _ => { });

        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<FakeMessage1>>(handler);
        services.AddSingleton(_mockBus.Object);
        var sp = services.BuildServiceProvider();

        var exceptionHandlerInvocations = 0;
        var mockConfig = new Mock<IBusConfiguration>();
        mockConfig.Setup(c => c.ExceptionHandler).Returns((Func<Exception, CancellationToken, ValueTask>)((ex, _) => { exceptionHandlerInvocations++; return ValueTask.CompletedTask; }));

        var scopeAccessor = new ConsumeScopeAccessor();
        var handlerRegistry = BuildHandlerRegistry(
            (typeof(FakeMessage1), typeof(TestDispatchHandler)),
            (typeof(PolyBaseMessage), typeof(PolyBaseHandler)));
        var processors = new List<IMessageProcessor>
        {
            new ReplyProcessor(_replyManager),
            new HandlerProcessor(handlerRegistry, scopeAccessor, new Lazy<IBus>(sp.GetRequiredService<IBus>), new BusConfiguration(), new QueueConfiguration(), new ConsumeContextPool(), new ConsumeContextAccessor(), Microsoft.Extensions.Logging.Abstractions.NullLogger<HandlerProcessor>.Instance)
        };
        var registry = CreateRegistryWithTypes(typeof(FakeMessage1), typeof(PolyBaseMessage), typeof(PolyDerivedMessage));
        var dispatcher = new MessageDispatcher(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            processors,
            NullLogger<MessageDispatcher>.Instance,
            mockConfig.Object,
            CreateEmptyPipelineConfig().Object,
            sp.GetRequiredService<IServiceScopeFactory>(),
            scopeAccessor,
            registry);

        var headers = MakeHeaders();
        var messageBytes = new byte[] { 1, 2, 3 };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert — OCE escapes (not swallowed as Success=false)
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            dispatcher.DispatchAsync(messageBytes, "FakeMessage1", headers, cts.Token));

        Assert.Equal(0, exceptionHandlerInvocations);
        _mockFilterPipeline.Verify(
            f => f.ExecuteAfterConsumingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Dispatch_DerivedMessageType_InvokesBaseTypeHandler()
    {
        // Arrange
        var message = new PolyDerivedMessage(Guid.NewGuid()) { Content = "base", Extra = "derived" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(PolyDerivedMessage))).Returns(message);

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
        var result = await dispatcher.DispatchAsync(messageBytes, "PolyDerivedMessage", headers);

        // Assert
        Assert.True(result.Success);
        Assert.True(handler.Invoked);
    }

    [Fact]
    public async Task Dispatch_NoProcessorHandlesMessage_ReturnsNotHandled()
    {
        // Registered type, serialised successfully, but no processor claims it. The dispatcher
        // reports Success=true so the consumer acks the broker, but flags NotHandled so the
        // host can DLQ it when DeadLetterUnhandledMessages is enabled.
        _mockSerializer
            .Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(new FakeMessage1(Guid.NewGuid()));

        var dispatcher = CreateDispatcherWithProcessors([]);
        var headers = MakeHeaders();

        var result = await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, "FakeMessage1", headers);

        Assert.True(result.Success);
        Assert.True(result.NotHandled);
    }

    [Fact]
    public async Task Dispatch_ProcessorHandlesMessage_DoesNotSetNotHandled()
    {
        _mockSerializer
            .Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1)))
            .Returns(new FakeMessage1(Guid.NewGuid()));

        var processor = new AlwaysHandledProcessor();
        var dispatcher = CreateDispatcherWithProcessors([processor]);
        var headers = MakeHeaders();

        var result = await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, "FakeMessage1", headers);

        Assert.True(result.Success);
        Assert.False(result.NotHandled);
    }

    [Fact]
    public async Task Dispatch_UnregisteredType_ReturnsNotHandled()
    {
        // Unregistered types are a terminal condition — retrying never resolves them.
        // The dispatcher routes them as not-handled (Success=true, NotHandled=true) so the
        // consumer host acks and either dead-letters or drops, rather than nack/requeue looping
        // through the full retry budget.
        var emptyRegistry = new MessageTypeRegistry();
        var sp = new ServiceCollection().BuildServiceProvider();
        var scopeAccessor = new ConsumeScopeAccessor();
        var processors = new List<IMessageProcessor>
        {
            new ReplyProcessor(_replyManager),
            new HandlerProcessor(BuildHandlerRegistry(), scopeAccessor, new Lazy<IBus>(() => new Mock<IBus>().Object), new BusConfiguration(), new QueueConfiguration(), new ConsumeContextPool(), new ConsumeContextAccessor(), Microsoft.Extensions.Logging.Abstractions.NullLogger<HandlerProcessor>.Instance)
        };
        var dispatcher = new MessageDispatcher(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            processors,
            NullLogger<MessageDispatcher>.Instance,
            new Mock<IBusConfiguration>().Object,
            CreateEmptyPipelineConfig().Object,
            sp.GetRequiredService<IServiceScopeFactory>(),
            scopeAccessor,
            emptyRegistry);

        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.FullTypeName] = Encoding.UTF8.GetBytes(typeof(FakeMessage1).AssemblyQualifiedName!)
        };

        // Act
        var result = await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, "FakeMessage1", headers);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.NotHandled);
    }

    // ---------------- messageType parameter is authoritative ----------------

    [Fact]
    public async Task Dispatch_WithMessageTypeParameter_AndNoHeader_ResolvesFromParameter()
    {
        // Transport honours the IMessageDispatcher contract by passing the wire type
        // name as the `messageType` parameter but does not stamp FullTypeName/TypeName.
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "FromParameter" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1))).Returns(message);

        FakeMessage1? receivedMessage = null;
        var handler = new TestDispatchHandler(onHandle: m => receivedMessage = m);
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<FakeMessage1>>(handler);
        services.AddSingleton(_mockBus.Object);
        var sp = services.BuildServiceProvider();

        var dispatcher = CreateDispatcher(sp);
        var headers = new Dictionary<string, object>(); // no FullTypeName, no TypeName

        var result = await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, typeof(FakeMessage1).AssemblyQualifiedName!, headers);

        Assert.True(result.Success);
        Assert.Same(message, receivedMessage);
    }

    [Fact]
    public async Task Dispatch_PrefersMessageTypeParameter_WhenBothProvided()
    {
        // Parameter = real registered type name. Header = bogus string.
        // The parameter must win, so the handler is invoked.
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "ParameterWins" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1))).Returns(message);

        bool handlerCalled = false;
        var handler = new TestDispatchHandler(onHandle: _ => handlerCalled = true);
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<FakeMessage1>>(handler);
        services.AddSingleton(_mockBus.Object);
        var sp = services.BuildServiceProvider();

        var dispatcher = CreateDispatcher(sp);
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.FullTypeName] = Encoding.UTF8.GetBytes("Bogus.Type.That.Is.Not.Registered")
        };

        var result = await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, typeof(FakeMessage1).AssemblyQualifiedName!, headers);

        Assert.True(result.Success);
        Assert.True(handlerCalled);
    }

    [Fact]
    public async Task Dispatch_FallsBackToHeader_WhenMessageTypeParameterEmpty()
    {
        // Existing RabbitMQ-host path: host has already pulled FullTypeName from the
        // header and passed it as messageType. But if a caller passes an empty/whitespace
        // messageType, fall back to the header (unchanged behaviour for legacy transports).
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "FallbackFromHeader" };
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1))).Returns(message);

        bool handlerCalled = false;
        var handler = new TestDispatchHandler(onHandle: _ => handlerCalled = true);
        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<FakeMessage1>>(handler);
        services.AddSingleton(_mockBus.Object);
        var sp = services.BuildServiceProvider();

        var dispatcher = CreateDispatcher(sp);
        var headers = MakeHeaders(); // has FullTypeName

        var result = await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, "", headers);

        Assert.True(result.Success);
        Assert.True(handlerCalled);
    }

    [Fact]
    public async Task Dispatch_ReturnsFailure_WhenParameterAndHeadersBothMissing()
    {
        // No parameter, no FullTypeName header, no TypeName header — we log and return
        // Success=false rather than throwing uncaught, so the broker can nack normally.
        var dispatcher = CreateDispatcher(new ServiceCollection().BuildServiceProvider());
        var headers = new Dictionary<string, object>();

        var result = await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, "", headers);

        Assert.False(result.Success);
        Assert.IsType<InvalidOperationException>(result.Exception);
    }

    // ---------------- Per-dispatch DI scope ----------------

    [Fact]
    public async Task Dispatch_CreatesFreshScope_AndDisposesAfterHandler()
    {
        // Per-message scope lifecycle: a scoped service resolved inside the dispatch
        // must be the same instance across resolutions in that dispatch, and the scope
        // must be disposed before Dispatch returns.
        var message = new FakeMessage1(Guid.NewGuid());
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1))).Returns(message);

        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<FakeMessage1>>(new TestDispatchHandler());
        services.AddSingleton(_mockBus.Object);
        services.AddScoped<DisposableMarker>();
        var sp = services.BuildServiceProvider();

        DisposableMarker? scopedFromProcessor1 = null;
        DisposableMarker? scopedFromProcessor2 = null;
        var scopeAccessor = new ConsumeScopeAccessor();
        var captureProcessor = new CapturingProcessor(scopedProvider =>
        {
            scopedFromProcessor1 = scopedProvider.GetRequiredService<DisposableMarker>();
            scopedFromProcessor2 = scopedProvider.GetRequiredService<DisposableMarker>();
        }, scopeAccessor);

        var dispatcher = new MessageDispatcher(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            [captureProcessor],
            NullLogger<MessageDispatcher>.Instance,
            new Mock<IBusConfiguration>().Object,
            CreateEmptyPipelineConfig().Object,
            sp.GetRequiredService<IServiceScopeFactory>(),
            scopeAccessor,
            CreateRegistryWithTypes(typeof(FakeMessage1)));

        var result = await dispatcher.DispatchAsync(new byte[] { 1, 2, 3 }, "FakeMessage1", MakeHeaders());

        Assert.True(result.Success);
        Assert.NotNull(scopedFromProcessor1);
        Assert.Same(scopedFromProcessor1, scopedFromProcessor2);
        Assert.True(scopedFromProcessor1!.Disposed, "Scoped service should have been disposed when the dispatch scope exited.");
    }

    [Fact]
    public async Task Dispatch_CreatesDistinctScopes_AcrossDispatches()
    {
        // Two back-to-back dispatches must receive independent scopes — a cached
        // middleware chain would pin the first scope for the life of the bus.
        var message = new FakeMessage1(Guid.NewGuid());
        _mockSerializer.Setup(s => s.Deserialize(It.IsAny<ReadOnlyMemory<byte>>(), typeof(FakeMessage1))).Returns(message);

        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<FakeMessage1>>(new TestDispatchHandler());
        services.AddSingleton(_mockBus.Object);
        services.AddScoped<DisposableMarker>();
        var sp = services.BuildServiceProvider();

        var captured = new List<DisposableMarker>();
        var scopeAccessor = new ConsumeScopeAccessor();
        var captureProcessor = new CapturingProcessor(scopedProvider =>
        {
            captured.Add(scopedProvider.GetRequiredService<DisposableMarker>());
        }, scopeAccessor);

        var dispatcher = new MessageDispatcher(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            [captureProcessor],
            NullLogger<MessageDispatcher>.Instance,
            new Mock<IBusConfiguration>().Object,
            CreateEmptyPipelineConfig().Object,
            sp.GetRequiredService<IServiceScopeFactory>(),
            scopeAccessor,
            CreateRegistryWithTypes(typeof(FakeMessage1)));

        await dispatcher.DispatchAsync(new byte[] { 1 }, "FakeMessage1", MakeHeaders());
        await dispatcher.DispatchAsync(new byte[] { 1 }, "FakeMessage1", MakeHeaders());

        Assert.Equal(2, captured.Count);
        Assert.NotSame(captured[0], captured[1]);
        Assert.True(captured[0].Disposed);
        Assert.True(captured[1].Disposed);
    }
}

file class AlwaysHandledProcessor : IMessageProcessor
{
    public Task<ProcessResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes,
        Type messageType,
        object? message,
        IDictionary<string, object> headers,
        Envelope envelope,
        CancellationToken cancellationToken = default)
        => Task.FromResult(ProcessResult.Handled);
}

file class PolyBaseMessage(Guid correlationId) : Message(correlationId)
{
    public string Content { get; set; } = string.Empty;
}

file class PolyDerivedMessage(Guid correlationId) : PolyBaseMessage(correlationId)
{
    public string Extra { get; set; } = string.Empty;
}

file class PolyBaseHandler : IMessageHandler<PolyBaseMessage>
{
    public bool Invoked { get; private set; }
    public Task HandleAsync(PolyBaseMessage message, IConsumeContext context, CancellationToken cancellationToken = default) { Invoked = true; return Task.CompletedTask; }
}

file sealed class UnregisteredReplyMessage(Guid correlationId) : Message(correlationId)
{
}

file sealed class DisposableMarker : IDisposable
{
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}

file sealed class CapturingProcessor(Action<IServiceProvider> capture, ConsumeScopeAccessor scopeAccessor) : IMessageProcessor
{
    private readonly Action<IServiceProvider> _capture = capture;
    private readonly ConsumeScopeAccessor _scopeAccessor = scopeAccessor;

    public bool RunBeforeDeserialization => false;

    public Task<ProcessResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes,
        Type messageType,
        object? message,
        IDictionary<string, object> headers,
        Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        _capture(_scopeAccessor.Current);
        return Task.FromResult(ProcessResult.Handled);
    }
}

file sealed class TestDispatcherReplyManager : IReplyStatusRequestReplyManager
{
    public int CallCount { get; private set; }
    public string? LastMessageId { get; private set; }
    public Type? LastMessageType { get; private set; }
    public bool ShouldHandleReplies { get; set; } = true;

    public bool TryProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type)
    {
        CallCount++;
        LastMessageId = messageId;
        LastMessageType = type;
        return ShouldHandleReplies;
    }

    public bool IsTrackedRequest(string messageId) => false;
}

file sealed class OrderRecordingPreDeserProcessor(List<string> order) : IMessageProcessor
{
    private readonly List<string> _order = order;

    public bool RunBeforeDeserialization => true;
    public bool ReturnHandled { get; set; }
    public int CallCount { get; private set; }

    public Task<ProcessResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes,
        Type messageType,
        object? message,
        IDictionary<string, object> headers,
        Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        _order.Add("pre-deser-processor");
        return Task.FromResult(ReturnHandled ? ProcessResult.Handled : ProcessResult.NotHandled);
    }
}
