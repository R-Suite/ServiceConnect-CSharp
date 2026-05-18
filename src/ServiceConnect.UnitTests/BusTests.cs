using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;
using ServiceConnect.UnitTests.Fakes;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests;

public class BusTests
{
    private readonly Mock<IMessageSerializer> _mockSerializer;
    private readonly Mock<IFilterPipeline> _mockFilterPipeline;
    private readonly Mock<ISendMessagePipeline> _mockSendPipeline;
    private readonly Mock<IRequestReplyManager> _mockRequestReplyManager;
    private readonly Mock<IBusConfiguration> _mockConfig;
    private readonly Mock<IPipelineConfiguration> _mockPipelineConfig;
    private readonly Mock<ILogger<Bus>> _mockLogger;
    private readonly Mock<IQueueConfiguration> _mockQueueConfig;
    private readonly Mock<IMessageDispatcher> _mockDispatcher;
    private readonly IReadOnlyList<HandlerReference> _handlerReferences;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConsumeScopeAccessor _scopeAccessor;
    private readonly Bus _bus;

    public BusTests()
    {
        _mockSerializer = new Mock<IMessageSerializer>();
        _mockFilterPipeline = new Mock<IFilterPipeline>();
        _mockSendPipeline = new Mock<ISendMessagePipeline>();
        _mockRequestReplyManager = new Mock<IRequestReplyManager>();
        _mockConfig = new Mock<IBusConfiguration>();
        _mockPipelineConfig = new Mock<IPipelineConfiguration>();
        // Default: no outgoing filters registered — Bus takes the fast path
        _mockPipelineConfig.Setup(x => x.OutgoingFilters).Returns([]);
        _mockLogger = new Mock<ILogger<Bus>>();
        _mockQueueConfig = new Mock<IQueueConfiguration>();
        _mockQueueConfig.Setup(x => x.QueueName).Returns("test-queue");

        // Default: filters pass through
        _mockFilterPipeline.Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(FilterAction.Continue);
        _mockSerializer.SetupSerializeAny<FakeMessage1>([1, 2, 3]);

        _mockDispatcher = new Mock<IMessageDispatcher>();
        _handlerReferences = [];
        _scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        _scopeAccessor = new ConsumeScopeAccessor();

        _bus = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            _mockPipelineConfig.Object,
            _scopeFactory,
            _scopeAccessor);
    }

    [Fact]
    public void IsConsuming_ShouldBeFalse_WhenNotConsuming()
    {
        Assert.False(_bus.IsConsuming);
    }

    [Fact]
    public async Task StartConsumingAsync_ShouldThrow_WhenNoConsumerRegistered()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _bus.StartConsumingAsync());
    }

    [Fact]
    public async Task StartConsumingAsync_ShouldSetIsConsumingToTrue_WhenConsumerRegistered()
    {
        // Arrange
        var mockConsumer = new Mock<IConsumer>();
        mockConsumer.Setup(x => x.StartConsumingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ConsumerEventHandler>()))
            .Returns(Task.CompletedTask);

        var bus = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            _mockPipelineConfig.Object,
            _scopeFactory,
            _scopeAccessor,
            mockConsumer.Object);

        // Act
        await bus.StartConsumingAsync();

        // Assert
        Assert.True(bus.IsConsuming);
    }

    [Fact]
    public async Task StopConsumingAsync_ShouldSetIsConsumingToFalse()
    {
        // StopConsuming can be called even without starting (no consumer needed)
        await _bus.StopConsumingAsync();
        Assert.False(_bus.IsConsuming);
    }

    [Fact]
    public async Task DisposeAsync_ShouldSetIsConsumingToFalse()
    {
        await _bus.DisposeAsync();
        Assert.False(_bus.IsConsuming);
    }

    [Fact]
    public async Task DisposeAsync_DoesNotBlockOnConsumerDispose()
    {
        // Bus.DisposeAsync no longer calls IConsumer.DisposeAsync — the IConsumer is a DI
        // singleton and the host's IServiceProvider disposes it on shutdown. Even if a hostile
        // mock would block on its own DisposeAsync, the Bus dispose path is decoupled from it.
        var releaseDispose = new TaskCompletionSource();
        var mockConsumer = new Mock<IConsumer>();
        mockConsumer
            .Setup(x => x.StartConsumingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ConsumerEventHandler>()))
            .Returns(Task.CompletedTask);
        mockConsumer
            .Setup(x => x.DisposeAsync())
            .Returns(new ValueTask(releaseDispose.Task));

        var bus = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            _mockPipelineConfig.Object,
            _scopeFactory,
            _scopeAccessor,
            mockConsumer.Object);

        await bus.StartConsumingAsync();

        var disposeTask = bus.DisposeAsync().AsTask();
        await Task.WhenAny(disposeTask, Task.Delay(500));

        Assert.True(disposeTask.IsCompleted);
        mockConsumer.Verify(x => x.DisposeAsync(), Times.Never);
    }

    [Fact]
    public async Task PublishAsync_ShouldSerializeAndPublish()
    {
        // Arrange — no outgoing filters (fast path; filter pipeline is not called)
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerialize(message, messageBytes);
        _mockSendPipeline.Setup(x => x.ExecutePublishMessagePipelineAsync(
            It.Is<SendContext>(ctx =>
                ctx.MessageType == typeof(FakeMessage1) &&
                ctx.MessageBytes.ToArray().SequenceEqual(messageBytes) &&
                ctx.EndPoint == null),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _bus.PublishAsync(message);

        // Assert
        _mockSerializer.VerifySerialize(message, Times.Once);
        _mockFilterPipeline.Verify(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockSendPipeline.Verify(x => x.ExecutePublishMessagePipelineAsync(
            It.Is<SendContext>(ctx =>
                ctx.MessageType == typeof(FakeMessage1) &&
                ctx.MessageBytes.ToArray().SequenceEqual(messageBytes) &&
                ctx.EndPoint == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WithOutgoingFilters_ShouldExecuteFilterPipeline()
    {
        // Arrange — bus created with outgoing filters registered; filter pipeline must be invoked
        var pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
        pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns([typeof(object)]);
        var busWithFilters = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            pipelineConfigWithFilter.Object,
            _scopeFactory,
            _scopeAccessor);

        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerialize(message, messageBytes);
        _mockSendPipeline.Setup(x => x.ExecutePublishMessagePipelineAsync(
            It.Is<SendContext>(ctx =>
                ctx.MessageType == typeof(FakeMessage1) &&
                ctx.MessageBytes.ToArray().SequenceEqual(messageBytes) &&
                ctx.EndPoint == null),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await busWithFilters.PublishAsync(message);

        // Assert
        _mockFilterPipeline.Verify(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockSendPipeline.Verify(x => x.ExecutePublishMessagePipelineAsync(
            It.Is<SendContext>(ctx =>
                ctx.MessageType == typeof(FakeMessage1) &&
                ctx.MessageBytes.ToArray().SequenceEqual(messageBytes) &&
                ctx.EndPoint == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WhenFilterBlocksMessage_ThrowsOutgoingFiltersBlocked()
    {
        // Arrange — must have outgoing filters registered so the filter pipeline is invoked
        _mockFilterPipeline.Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(FilterAction.Stop);
        var pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
        pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns([typeof(object)]);
        var busWithFilters = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            pipelineConfigWithFilter.Object,
            _scopeFactory,
            _scopeAccessor);
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        _mockSerializer.SetupSerialize<FakeMessage1>(message, [1, 2, 3]);

        var ex = await Assert.ThrowsAsync<OutgoingFiltersBlockedException>(
            () => busWithFilters.PublishAsync(message));

        Assert.Contains("published", ex.Message, StringComparison.OrdinalIgnoreCase);
        _mockSendPipeline.Verify(x => x.ExecutePublishMessagePipelineAsync(
            It.IsAny<SendContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PublishAsync_WithRoutingKey_ShouldIncludeRoutingKeyInHeaders()
    {
        // Arrange
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        var options = new PublishOptions { RoutingKey = "my-routing-key" };

        _mockSendPipeline.Setup(x => x.ExecutePublishMessagePipelineAsync(
            It.Is<SendContext>(ctx => ctx.EndPoint == null),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _bus.PublishAsync(message, options);

        // Assert
        _mockSendPipeline.Verify(x => x.ExecutePublishMessagePipelineAsync(
            It.Is<SendContext>(ctx =>
                ctx.MessageType == typeof(FakeMessage1) &&
                ctx.EndPoint == null &&
                ctx.Headers.ContainsKey("RoutingKey") &&
                ctx.Headers["RoutingKey"] == "my-routing-key"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WithRoutingKey_WhenProducerDoesNotSupportIt_LogsWarningOnce()
    {
        // A producer whose SupportsRoutingKey returns false should trigger exactly one
        // LogWarning per Bus instance so operators see the silent-drop scenario once,
        // even when PublishAsync is called multiple times with a routing key.
        var mockProducer = new Mock<IProducer>();
        mockProducer.Setup(x => x.SupportsRoutingKey).Returns(false);
        var mockLogger = new Mock<ILogger<Bus>>();
        var busWithShimProducer = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            _mockPipelineConfig.Object,
            _scopeFactory,
            _scopeAccessor,
            producer: mockProducer.Object);

        _mockSendPipeline
            .Setup(x => x.ExecutePublishMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        var options = new PublishOptions { RoutingKey = "some-key" };

        // Call twice — the warning must fire exactly once (once-per-bus latch).
        await busWithShimProducer.PublishAsync(message, options);
        await busWithShimProducer.PublishAsync(message, options);

        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("SupportsRoutingKey=false")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WithRoutingKey_WhenProducerSupportsIt_DoesNotLogWarning()
    {
        // A producer with SupportsRoutingKey=true must never trigger the shim-drop warning,
        // even when a routing key is supplied on every call.
        var mockProducer = new Mock<IProducer>();
        mockProducer.Setup(x => x.SupportsRoutingKey).Returns(true);
        var mockLogger = new Mock<ILogger<Bus>>();
        var busWithRealProducer = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            _mockPipelineConfig.Object,
            _scopeFactory,
            _scopeAccessor,
            producer: mockProducer.Object);

        _mockSendPipeline
            .Setup(x => x.ExecutePublishMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        var options = new PublishOptions { RoutingKey = "some-key" };

        await busWithRealProducer.PublishAsync(message, options);

        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("SupportsRoutingKey=false")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public void PublishOptions_IsReadonlyRecordStruct()
    {
        // PublishOptions is a readonly record struct so each PublishAsync call
        // captures a snapshot by value. A mutable sealed class would let a
        // caller mutate Headers/RoutingKey on a shared instance while a
        // concurrent PublishAsync was reading them mid-flight.
        var type = typeof(PublishOptions);

        Assert.True(type.IsValueType);
        Assert.True(type.GetMethod("<Clone>$", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) is null
                    || type.GetMethods().Any(m => m.Name == "Equals" && m.ReturnType == typeof(bool)),
            "record semantics expected");
        foreach (var prop in type.GetProperties())
        {
            var setter = prop.SetMethod;
            Assert.NotNull(setter);
            var modreqs = setter!.ReturnParameter.GetRequiredCustomModifiers();
            Assert.Contains(modreqs, t => t.Name == "IsExternalInit");
        }
    }

    [Fact]
    public void SendAndPublishOptions_HeadersTypedAsReadOnlyDictionary()
    {
        // The Headers contract is IReadOnlyDictionary<string,string>? so the
        // type system prevents callers from sharing a mutable instance and
        // concurrently mutating it during BuildHeadersDirect's foreach, which
        // would throw "Collection was modified" from inside the send path.
        Assert.Equal(
            typeof(IReadOnlyDictionary<string, string>),
            typeof(SendOptions).GetProperty(nameof(SendOptions.Headers))!.PropertyType);
        Assert.Equal(
            typeof(IReadOnlyDictionary<string, string>),
            typeof(PublishOptions).GetProperty(nameof(PublishOptions.Headers))!.PropertyType);
    }

    [Fact]
    public async Task PublishAsync_HeadersPostMutationDoesNotAffectInFlightSend()
    {
        // BuildHeadersDirect must snapshot the caller's headers before handing
        // them to the send pipeline. Mutations to the caller's dictionary after
        // PublishAsync returns must not reach the transport. Verify by capturing
        // the dictionary the pipeline receives, then mutating the source and
        // asserting the captured view is unchanged.
        var sharedHeaders = new Dictionary<string, string> { ["caller-header"] = "original" };
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };

        IDictionary<string, string>? captured = null;
        _mockSendPipeline.Setup(x => x.ExecutePublishMessagePipelineAsync(
            It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) => captured = ctx.Headers)
            .Returns(Task.CompletedTask);

        await _bus.PublishAsync(message, new PublishOptions { Headers = sharedHeaders });

        Assert.NotNull(captured);
        Assert.Equal("original", captured!["caller-header"]);

        // caller mutation post-publish must not reach back into the captured map
        sharedHeaders["caller-header"] = "after-send";
        sharedHeaders["new-key"] = "late";

        Assert.Equal("original", captured["caller-header"]);
        Assert.False(captured.ContainsKey("new-key"));
    }

    [Fact]
    public async Task PublishAsync_FastPath_StampsCorrelationIdHeader()
    {
        var correlationId = Guid.NewGuid();
        var message = new FakeMessage1(correlationId) { Username = "Tim" };

        await _bus.PublishAsync(message);

        _mockSendPipeline.Verify(x => x.ExecutePublishMessagePipelineAsync(
            It.Is<SendContext>(ctx =>
                ctx.MessageType == typeof(FakeMessage1) &&
                ctx.EndPoint == null &&
                ctx.Headers.ContainsKey(HeaderKeys.CorrelationId) &&
                ctx.Headers[HeaderKeys.CorrelationId] == correlationId.ToString()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_FilterPath_StampsCorrelationIdHeader()
    {
        var correlationId = Guid.NewGuid();
        var pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
        pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns([typeof(object)]);
        var busWithFilters = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            pipelineConfigWithFilter.Object,
            _scopeFactory,
            _scopeAccessor);
        var message = new FakeMessage1(correlationId) { Username = "Tim" };

        await busWithFilters.PublishAsync(message);

        _mockSendPipeline.Verify(x => x.ExecutePublishMessagePipelineAsync(
            It.Is<SendContext>(ctx =>
                ctx.MessageType == typeof(FakeMessage1) &&
                ctx.EndPoint == null &&
                ctx.Headers.ContainsKey(HeaderKeys.CorrelationId) &&
                ctx.Headers[HeaderKeys.CorrelationId] == correlationId.ToString()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_WithOutgoingFilters_RunsFilterInOwnScope()
    {
        // Each outbound call must establish a fresh per-call DI scope so filters
        // can resolve scoped services. The accessor must see a non-null Current
        // during filter execution and revert once the filter returns.
        var services = new ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var scopeAccessor = new ConsumeScopeAccessor();

        IServiceProvider? providerDuringFilter = null;
        _mockFilterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                providerDuringFilter = scopeAccessor.Current;
                return FilterAction.Continue;
            });

        var pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
        pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns([typeof(object)]);

        var busWithFilters = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            pipelineConfigWithFilter.Object,
            scopeFactory,
            scopeAccessor);

        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };

        await busWithFilters.PublishAsync(message);

        Assert.NotNull(providerDuringFilter);
        // Accessor must revert to no-scope once the filter returns.
        Assert.Throws<InvalidOperationException>(() => _ = scopeAccessor.Current);
    }

    [Fact]
    public async Task PublishAsync_TwoCalls_CreateDistinctOutgoingFilterScopes()
    {
        // Per-call isolation: the second call must not share a scope with the first.
        var services = new ServiceCollection();
        services.AddScoped<MarkerProbe>();
        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var scopeAccessor = new ConsumeScopeAccessor();

        var seenMarkers = new List<MarkerProbe>();
        _mockFilterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                seenMarkers.Add(scopeAccessor.Current.GetRequiredService<MarkerProbe>());
                return FilterAction.Continue;
            });

        var pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
        pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns([typeof(object)]);

        var busWithFilters = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            pipelineConfigWithFilter.Object,
            scopeFactory,
            scopeAccessor);

        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };

        await busWithFilters.PublishAsync(message);
        await busWithFilters.PublishAsync(message);

        Assert.Equal(2, seenMarkers.Count);
        Assert.NotSame(seenMarkers[0], seenMarkers[1]);
    }

    [Fact]
    public async Task SendAsync_StampsCorrelationIdHeader()
    {
        var correlationId = Guid.NewGuid();
        var message = new FakeMessage1(correlationId) { Username = "Tim" };

        await _bus.SendAsync(message);

        _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
            It.Is<SendContext>(ctx =>
                ctx.MessageType == typeof(FakeMessage1) &&
                ctx.EndPoint == null &&
                ctx.Headers.ContainsKey(HeaderKeys.CorrelationId) &&
                ctx.Headers[HeaderKeys.CorrelationId] == correlationId.ToString()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendRequestAsync_StampsCorrelationIdHeader()
    {
        var correlationId = Guid.NewGuid();
        var message = new FakeMessage1(correlationId) { Username = "Tim" };
        _mockRequestReplyManager.Setup(x => x.SendRequestAsync<FakeMessage1, FakeMessage1>(
                It.IsAny<FakeMessage1>(),
                It.IsAny<Dictionary<string, string>>(),
                It.IsAny<RequestOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(message);

        await _bus.SendRequestAsync<FakeMessage1, FakeMessage1>(message);

        _mockRequestReplyManager.Verify(x => x.SendRequestAsync<FakeMessage1, FakeMessage1>(
            message,
            It.Is<Dictionary<string, string>>(h =>
                h.ContainsKey(HeaderKeys.CorrelationId) &&
                h[HeaderKeys.CorrelationId] == correlationId.ToString()),
            It.IsAny<RequestOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendAsync_ShouldSerializeAndSend()
    {
        // Arrange
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        var messageBytes = new byte[] { 1, 2, 3 };
        _mockSerializer.SetupSerialize(message, messageBytes);
        _mockSendPipeline.Setup(x => x.ExecuteSendMessagePipelineAsync(
            It.Is<SendContext>(ctx =>
                ctx.MessageType == typeof(FakeMessage1) &&
                ctx.MessageBytes.ToArray().SequenceEqual(messageBytes) &&
                ctx.EndPoint == null),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _bus.SendAsync(message);

        // Assert
        _mockSerializer.VerifySerialize(message, Times.Once);
        _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
            It.Is<SendContext>(ctx =>
                ctx.MessageType == typeof(FakeMessage1) &&
                ctx.MessageBytes.ToArray().SequenceEqual(messageBytes) &&
                ctx.EndPoint == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendAsync_WithEndPoint_ShouldSendToEndPoint()
    {
        // Arrange
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        var options = new SendOptions { EndPoint = "MyEndPoint" };

        _mockSendPipeline.Setup(x => x.ExecuteSendMessagePipelineAsync(
            It.Is<SendContext>(ctx => ctx.EndPoint == "MyEndPoint"),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _bus.SendAsync(message, options);

        // Assert
        _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
            It.Is<SendContext>(ctx =>
                ctx.MessageType == typeof(FakeMessage1) &&
                ctx.EndPoint == "MyEndPoint"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendToManyAsync_FansOut_ToEachEndpoint()
    {
        // Arrange
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        var capturedEndpoints = new List<string?>();
        var capturedHeaders = new List<IDictionary<string, string>>();

        _mockSendPipeline
            .Setup(x => x.ExecuteSendMessagePipelineAsync(It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) =>
            {
                capturedEndpoints.Add(ctx.EndPoint);
                capturedHeaders.Add(ctx.Headers);
            })
            .Returns(Task.CompletedTask);

        // Act
        await _bus.SendToManyAsync(message, ["queue-a", "queue-b", "queue-c"]);

        // Assert: pipeline called once per endpoint, in order
        Assert.Equal(3, capturedEndpoints.Count);
        Assert.Equal("queue-a", capturedEndpoints[0]);
        Assert.Equal("queue-b", capturedEndpoints[1]);
        Assert.Equal("queue-c", capturedEndpoints[2]);

        // Serialiser called exactly once — fan-out reuses the bytes across iterations.
        _mockSerializer.Verify(x => x.Serialize(It.IsAny<FakeMessage1>(), It.IsAny<IBufferWriter<byte>>()), Times.Once);

        // Each iteration receives a distinct Headers dictionary so middleware cannot
        // leak per-endpoint mutations into the next iteration.
        Assert.NotSame(capturedHeaders[0], capturedHeaders[1]);
        Assert.NotSame(capturedHeaders[1], capturedHeaders[2]);
        Assert.NotSame(capturedHeaders[0], capturedHeaders[2]);
    }

    [Fact]
    public async Task SendToManyAsync_EmptyEndpointList_Throws()
    {
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _bus.SendToManyAsync(message, []));

        Assert.Contains("at least one endpoint", ex.Message);

        _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
            It.IsAny<SendContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SendToManyAsync_NullEndpointList_ThrowsArgumentNull()
    {
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _bus.SendToManyAsync(message, null!));

        _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
            It.IsAny<SendContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SendAsync_WhenFilterBlocksMessage_ThrowsOutgoingFiltersBlocked()
    {
        // Arrange — must have outgoing filters registered so the filter pipeline is invoked
        _mockFilterPipeline.Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>())).ReturnsAsync(FilterAction.Stop);
        var pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
        pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns([typeof(object)]);
        var busWithFilters = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            pipelineConfigWithFilter.Object,
            _scopeFactory,
            _scopeAccessor);
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        _mockSerializer.SetupSerialize<FakeMessage1>(message, [1, 2, 3]);

        var ex = await Assert.ThrowsAsync<OutgoingFiltersBlockedException>(
            () => busWithFilters.SendAsync(message));

        Assert.Contains("sent", ex.Message, StringComparison.OrdinalIgnoreCase);
        _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
            It.IsAny<SendContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PublishRequestAsync_DelegatesToRequestReplyManagerPublishMethod()
    {
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        var messageBytes = new byte[] { 1, 2, 3 };
        var options = new RequestOptions
        {
            Headers = new Dictionary<string, string>
            {
                ["CustomHeader"] = "CustomValue"
            }
        };

        _mockSerializer.SetupSerialize(message, messageBytes);
        _mockRequestReplyManager.Setup(x => x.PublishRequestAsync<FakeMessage1, FakeMessage1>(
                message,
                It.Is<Dictionary<string, string>>(h => h.ContainsKey("CustomHeader") && h["CustomHeader"] == "CustomValue"),
                options,
                It.IsAny<Action<FakeMessage1>>(),
                CancellationToken.None))
            .Returns(Task.CompletedTask);

        await _bus.PublishRequestAsync<FakeMessage1, FakeMessage1>(message, _ => { }, options);

        _mockRequestReplyManager.Verify(x => x.PublishRequestAsync<FakeMessage1, FakeMessage1>(
                message,
                It.Is<Dictionary<string, string>>(h => h.ContainsKey("CustomHeader") && h["CustomHeader"] == "CustomValue"),
                options,
                It.IsAny<Action<FakeMessage1>>(),
                CancellationToken.None),
            Times.Once);
        _mockRequestReplyManager.Verify(x => x.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(
                It.IsAny<FakeMessage1>(),
                It.IsAny<Dictionary<string, string>>(),
                It.IsAny<RequestOptions>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PublishRequestAsync_WithEndPoint_ThrowsArgumentException()
    {
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        var options = new RequestOptions { EndPoint = "MyEndPoint" };

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _bus.PublishRequestAsync<FakeMessage1, FakeMessage1>(message, _ => { }, options));

        Assert.Equal("options", ex.ParamName);
    }

    [Fact]
    public async Task PublishRequestAsync_WithEmptyEndPoint_DelegatesToRequestReplyManagerPublishMethod()
    {
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        var messageBytes = new byte[] { 1, 2, 3 };
        var options = new RequestOptions { EndPoint = string.Empty };

        _mockSerializer.SetupSerialize(message, messageBytes);
        _mockRequestReplyManager.Setup(x => x.PublishRequestAsync<FakeMessage1, FakeMessage1>(
                message,
                It.IsAny<Dictionary<string, string>>(),
                options,
                It.IsAny<Action<FakeMessage1>>(),
                CancellationToken.None))
            .Returns(Task.CompletedTask);

        await _bus.PublishRequestAsync<FakeMessage1, FakeMessage1>(message, _ => { }, options);

        _mockRequestReplyManager.Verify(x => x.PublishRequestAsync<FakeMessage1, FakeMessage1>(
                message,
                It.IsAny<Dictionary<string, string>>(),
                options,
                It.IsAny<Action<FakeMessage1>>(),
                CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task PublishRequestAsync_WhenFilterBlocksMessage_ThrowsOutgoingFiltersBlocked()
    {
        _mockFilterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Stop);

        var pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
        pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns([typeof(object)]);

        var busWithFilters = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            pipelineConfigWithFilter.Object,
            _scopeFactory,
            _scopeAccessor);

        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };

        await Assert.ThrowsAsync<OutgoingFiltersBlockedException>(
            () => busWithFilters.PublishRequestAsync<FakeMessage1, FakeMessage1>(message, _ => { }));
    }

    [Fact]
    public async Task SendRequestAsync_WhenFilterBlocksMessage_ThrowsOutgoingFiltersBlocked()
    {
        _mockFilterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Stop);

        var pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
        pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns([typeof(object)]);

        var busWithFilters = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            pipelineConfigWithFilter.Object,
            _scopeFactory,
            _scopeAccessor);

        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };

        var ex = await Assert.ThrowsAsync<OutgoingFiltersBlockedException>(
            () => busWithFilters.SendRequestAsync<FakeMessage1, FakeMessage1>(message));

        Assert.Equal("Outgoing filters blocked the request message.", ex.Message);
    }

    [Fact]
    public async Task SendRequestMultiAsync_WhenFilterBlocksMessage_ThrowsOutgoingFiltersBlocked()
    {
        _mockFilterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Stop);

        var pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
        pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns([typeof(object)]);

        var busWithFilters = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            pipelineConfigWithFilter.Object,
            _scopeFactory,
            _scopeAccessor);

        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };

        var ex = await Assert.ThrowsAsync<OutgoingFiltersBlockedException>(
            () => busWithFilters.SendRequestMultiAsync<FakeMessage1, FakeMessage1>(message));

        Assert.Equal("Outgoing filters blocked the request message.", ex.Message);
    }

    [Fact]
    public async Task SendToManyAsync_WhenFilterBlocksMessage_ThrowsOutgoingFiltersBlocked()
    {
        _mockFilterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Stop);

        var pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
        pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns([typeof(object)]);

        var busWithFilters = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            pipelineConfigWithFilter.Object,
            _scopeFactory,
            _scopeAccessor);

        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        _mockSerializer.SetupSerialize<FakeMessage1>(message, [1, 2, 3]);

        var ex = await Assert.ThrowsAsync<OutgoingFiltersBlockedException>(
            () => busWithFilters.SendToManyAsync(message, ["endpoint1", "endpoint2"]));

        Assert.Contains("multi-endpoint send", ex.Message, StringComparison.OrdinalIgnoreCase);
        _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
            It.IsAny<SendContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RouteAsync_WhenFilterBlocksMessage_ThrowsOutgoingFiltersBlocked()
    {
        _mockFilterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FilterAction.Stop);

        var pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
        pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns([typeof(object)]);

        var busWithFilters = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            pipelineConfigWithFilter.Object,
            _scopeFactory,
            _scopeAccessor);

        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        _mockSerializer.SetupSerialize<FakeMessage1>(message, [1, 2, 3]);

        var ex = await Assert.ThrowsAsync<OutgoingFiltersBlockedException>(
            () => busWithFilters.RouteAsync(message, ["destination1"]));

        Assert.Contains("routed", ex.Message, StringComparison.OrdinalIgnoreCase);
        _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
            It.IsAny<SendContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestTimeoutAsync_WithAmbientConsumeHeaders_PreservesCustomHeadersOnly()
    {
        TimeoutData? captured = null;
        var timeoutStore = new Mock<ITimeoutStore>();
        timeoutStore
            .Setup(x => x.InsertTimeoutAsync(It.IsAny<TimeoutData>(), It.IsAny<CancellationToken>()))
            .Callback<TimeoutData, CancellationToken>((data, _) => captured = data)
            .Returns(Task.CompletedTask);

        var incomingHeaders = new Dictionary<string, object>
        {
            ["Custom"] = "value",
            [HeaderKeys.RetryCount] = 3,
            [HeaderKeys.MessageId] = "managed-message-id",
            [HeaderKeys.SourceAddress] = "reply-queue"
        };

        var accessor = CreateConsumeContextAccessorOrFail();
        await using var bus = CreateBusWithTimeoutStoreAndAccessorOrFail(timeoutStore.Object, accessor);
        using var scope = PushConsumeContextOrFail(accessor, incomingHeaders);

        await bus.RequestTimeoutAsync(Guid.NewGuid(), TimeSpan.FromMinutes(1));

        Assert.NotNull(captured);
        Assert.Equal("test-queue", captured!.Destination);
        Assert.Equal("value", captured.Headers["Custom"]);
        Assert.False(captured.Headers.ContainsKey(HeaderKeys.RetryCount));
        Assert.False(captured.Headers.ContainsKey(HeaderKeys.MessageId));
        Assert.False(captured.Headers.ContainsKey(HeaderKeys.SourceAddress));
    }

    [Fact]
    public async Task RouteAsync_ShouldSendToFirstDestination_WithRoutingSlipForRemaining()
    {
        // Arrange
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        var destinations = new List<string> { "Dest1", "Dest2", "Dest3" };

        _mockSendPipeline.Setup(x => x.ExecuteSendMessagePipelineAsync(
            It.Is<SendContext>(ctx => ctx.EndPoint == "Dest1"),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _bus.RouteAsync(message, destinations);

        // Assert
        _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
            It.Is<SendContext>(ctx =>
                ctx.MessageType == typeof(FakeMessage1) &&
                ctx.EndPoint == "Dest1" &&
                ctx.Headers.ContainsKey("RoutingSlip") &&
                ctx.Headers["RoutingSlip"] == "Dest2,Dest3"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RouteAsync_ShouldThrow_WhenNoDestinations()
    {
        // Arrange
        var message = new FakeMessage1(Guid.NewGuid());

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _bus.RouteAsync(message, []));
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenDependencyIsNull()
    {
        var p = _mockPipelineConfig.Object;
        var sf = _scopeFactory;
        var sa = _scopeAccessor;
        Assert.Throws<ArgumentNullException>(() => new Bus(null!, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, p, sf, sa));
        Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, null!, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, p, sf, sa));
        Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, null!, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, p, sf, sa));
        Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, null!, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, p, sf, sa));
        Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, null!, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, p, sf, sa));
        Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, null!, _mockDispatcher.Object, _handlerReferences, p, sf, sa));
        Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, (IMessageDispatcher)null!, _handlerReferences, p, sf, sa));
        Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, null!, p, sf, sa));
        Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, null!, sf, sa));
        Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, p, null!, sa));
        Assert.Throws<ArgumentNullException>(() => new Bus(_mockSerializer.Object, _mockFilterPipeline.Object, _mockSendPipeline.Object, _mockRequestReplyManager.Object, _mockLogger.Object, _mockQueueConfig.Object, _mockDispatcher.Object, _handlerReferences, p, sf, null!));
    }

    // --- MessageId authority tests ---

    [Fact]
    public async Task SendAsync_FastPath_StampsMessageIdHeader()
    {
        // Bus is Bus-authoritative for MessageId; the fast path (no outgoing filters)
        // must stamp a non-empty MessageId even when the caller does not supply one.
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };

        await _bus.SendAsync(message);

        _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
            It.Is<SendContext>(ctx =>
                ctx.MessageType == typeof(FakeMessage1) &&
                ctx.EndPoint == null &&
                ctx.Headers.ContainsKey(HeaderKeys.MessageId) &&
                !string.IsNullOrEmpty(ctx.Headers[HeaderKeys.MessageId])),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendAsync_FastPath_CallerCannotOverride_MessageId()
    {
        // Bus stamps MessageId last, so a caller-supplied value in options.Headers
        // must be replaced by the Bus-minted GUID.
        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        var hostile = "00000000-0000-0000-0000-000000000000";
        var options = new SendOptions
        {
            Headers = new Dictionary<string, string> { [HeaderKeys.MessageId] = hostile }
        };

        await _bus.SendAsync(message, options);

        _mockSendPipeline.Verify(x => x.ExecuteSendMessagePipelineAsync(
            It.Is<SendContext>(ctx =>
                ctx.MessageType == typeof(FakeMessage1) &&
                ctx.EndPoint == null &&
                ctx.Headers.ContainsKey(HeaderKeys.MessageId) &&
                ctx.Headers[HeaderKeys.MessageId] != hostile),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_FilterPath_OutgoingFilterSeesNonEmptyMessageId()
    {
        // Outgoing filters must be able to read envelope.Headers["MessageId"]
        // without a KeyNotFoundException.
        Envelope? capturedEnvelope = null;

        _mockFilterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Envelope env, CancellationToken _) =>
            {
                capturedEnvelope = env;
                return FilterAction.Continue;
            });

        var pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
        pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns([typeof(object)]);

        var busWithFilters = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            pipelineConfigWithFilter.Object,
            _scopeFactory,
            _scopeAccessor);

        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };

        await busWithFilters.PublishAsync(message);

        Assert.NotNull(capturedEnvelope);
        Assert.True(capturedEnvelope!.Headers.ContainsKey(HeaderKeys.MessageId));
        var messageId = capturedEnvelope.Headers[HeaderKeys.MessageId]?.ToString();
        Assert.NotNull(messageId);
        Assert.NotEmpty(messageId!);
    }

    [Fact]
    public async Task PublishAsync_FilterPath_CallerCannotOverride_MessageId()
    {
        // Even on the filter path, the Bus must stamp MessageId last so a caller
        // cannot spoof it via options.Headers.
        var hostile = "00000000-0000-0000-0000-000000000000";
        string? seenMessageId = null;

        _mockFilterPipeline
            .Setup(x => x.ExecuteOutgoingFiltersAsync(It.IsAny<Envelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Envelope env, CancellationToken _) =>
            {
                seenMessageId = env.Headers[HeaderKeys.MessageId]?.ToString();
                return FilterAction.Continue;
            });

        var pipelineConfigWithFilter = new Mock<IPipelineConfiguration>();
        pipelineConfigWithFilter.Setup(x => x.OutgoingFilters).Returns([typeof(object)]);

        var busWithFilters = new Bus(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            pipelineConfigWithFilter.Object,
            _scopeFactory,
            _scopeAccessor);

        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        var options = new PublishOptions
        {
            Headers = new Dictionary<string, string> { [HeaderKeys.MessageId] = hostile }
        };

        await busWithFilters.PublishAsync(message, options);

        Assert.NotNull(seenMessageId);
        Assert.NotEqual(hostile, seenMessageId);
    }

    // --- Reserved header spoof-proofing ---

    [Fact]
    public async Task SendAsync_CallerCannotOverride_CorrelationIdOrMessageId()
    {
        // CorrelationId and MessageId are Bus-authoritative; caller-supplied values are
        // silently replaced by Bus-generated ones so consumers cannot be spoofed.
        var spoofedMessageId = Guid.NewGuid().ToString();
        var options = new SendOptions
        {
            Headers = new Dictionary<string, string>
            {
                [HeaderKeys.CorrelationId] = "spoofed-correlation",
                [HeaderKeys.MessageId] = spoofedMessageId,
            }
        };

        IDictionary<string, string>? captured = null;
        _mockSendPipeline
            .Setup(x => x.ExecuteSendMessagePipelineAsync(
                It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) => captured = ctx.Headers)
            .Returns(Task.CompletedTask);

        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        await _bus.SendAsync(message, options, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.NotEqual("spoofed-correlation", captured![HeaderKeys.CorrelationId]);
        Assert.NotEqual(spoofedMessageId, captured[HeaderKeys.MessageId]);
    }

    [Fact]
    public async Task SendAsync_CallerSuppliesMessageType_FlowsThroughToProducer()
    {
        // MessageType is not Bus-reserved; the caller-supplied value is forwarded so
        // OutboundHeaderBuilder (the authoritative stamper) can overwrite it with the
        // correct operation name on the wire.
        var options = new SendOptions
        {
            Headers = new Dictionary<string, string>
            {
                [HeaderKeys.MessageType] = "SomeoneElsesType",
            }
        };

        IDictionary<string, string>? captured = null;
        _mockSendPipeline
            .Setup(x => x.ExecuteSendMessagePipelineAsync(
                It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Callback<SendContext, CancellationToken>((ctx, _) => captured = ctx.Headers)
            .Returns(Task.CompletedTask);

        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        await _bus.SendAsync(message, options, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("SomeoneElsesType", captured![HeaderKeys.MessageType]);
    }

    [Fact]
    public async Task SendAsync_CallerSuppliesReservedHeader_LogsWarning()
    {
        // Supplying a still-reserved key (CorrelationId) must trigger exactly one
        // LogWarning that names the offending key, so operators can diagnose
        // misconfigured callers without silently swallowing the bad input.
        var options = new SendOptions
        {
            Headers = new Dictionary<string, string>
            {
                [HeaderKeys.CorrelationId] = "spoofed-id",
            }
        };

        _mockSendPipeline
            .Setup(x => x.ExecuteSendMessagePipelineAsync(
                It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        await _bus.SendAsync(message, options, CancellationToken.None);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(HeaderKeys.CorrelationId)),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task SendAsync_CallerSuppliesMessageType_NoWarningLogged()
    {
        // MessageType is not in the Bus's reserved set, so no warning is emitted when a
        // caller supplies it in options.Headers.  The producer is the authoritative stamper
        // and will overwrite it with the operation name on the wire.
        var options = new SendOptions
        {
            Headers = new Dictionary<string, string>
            {
                [HeaderKeys.MessageType] = "SomeoneElsesType",
            }
        };

        _mockSendPipeline
            .Setup(x => x.ExecuteSendMessagePipelineAsync(
                It.IsAny<SendContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var message = new FakeMessage1(Guid.NewGuid()) { Username = "Tim" };
        await _bus.SendAsync(message, options, CancellationToken.None);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(HeaderKeys.MessageType)),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    // --- Semaphore dispose race ---

    // Pins the ThrowIfDisposed()-before-semaphore ordering on the lifecycle entry points.
    [Fact]
    public async Task StartConsumingAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // After DisposeAsync, all lifecycle calls must throw ObjectDisposedException
        // (not NullReferenceException or succeed silently).
        var mockConsumer = new Mock<IConsumer>();
        var bus = CreateBusWithConsumer(mockConsumer.Object);
        await bus.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => bus.StartConsumingAsync(CancellationToken.None));
    }

    // --- Cancellation during StopConsuming surfaces OCE; transport dispose is DI's job ---

    [Fact]
    public async Task StopConsumingAsync_WhenCancellationRequested_DoesNotDisposeConsumer()
    {
        // The IConsumer is a DI singleton; the host's IServiceProvider disposes it on shutdown.
        // A cancelled StopConsuming must surface the cancellation but must NOT touch the
        // transport — there is no Bus-owned dispose path on the consumer any more.
        var mockConsumer = new Mock<IConsumer>();
        mockConsumer
            .Setup(x => x.StartConsumingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ConsumerEventHandler>()))
            .Returns(Task.CompletedTask);
        mockConsumer
            .Setup(x => x.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        var bus = CreateBusWithConsumer(mockConsumer.Object);
        await bus.StartConsumingAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancel so WaitAsync sees cancellation immediately

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => bus.StopConsumingAsync(cts.Token));

        mockConsumer.Verify(x => x.DisposeAsync(), Times.Never);

        // Explicit dispose to clean up the bus's own owned resources (semaphore + send pipeline).
        await bus.DisposeAsync();
        mockConsumer.Verify(x => x.DisposeAsync(), Times.Never);
    }

    // --- StopConsuming before start must not poison _stopped ---

    [Fact]
    public async Task StopConsumingAsync_BeforeStart_AllowsSubsequentStart()
    {
        // A defensive stop on an unstarted bus must leave it restartable.
        var mockConsumer = new Mock<IConsumer>();
        mockConsumer
            .Setup(x => x.StartConsumingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ConsumerEventHandler>()))
            .Returns(Task.CompletedTask);

        await using var bus = CreateBusWithConsumer(mockConsumer.Object);

        // Defensive stop before any start
        await bus.StopConsumingAsync(CancellationToken.None);

        // Must not throw "bus has been stopped"
        await bus.StartConsumingAsync(CancellationToken.None);
        await bus.StopConsumingAsync(CancellationToken.None); // cleanup
    }

    [Fact]
    public async Task StartConsumingAsync_AfterStop_ThrowsInvalidOperationException()
    {
        // The bus permanently latches _stopped after StopConsumingAsync. A second
        // StartConsumingAsync call must throw — documented contract; container/
        // orchestrator reuse of the instance after stop must surface immediately.
        var mockConsumer = new Mock<IConsumer>();
        mockConsumer
            .Setup(x => x.StartConsumingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ConsumerEventHandler>()))
            .Returns(Task.CompletedTask);

        await using var bus = CreateBusWithConsumer(mockConsumer.Object);

        await bus.StartConsumingAsync(CancellationToken.None);
        await bus.StopConsumingAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => bus.StartConsumingAsync(CancellationToken.None));
    }

    // --- Helper ---

    private Bus CreateBusWithConsumer(IConsumer consumer) =>
        new(
            _mockSerializer.Object,
            _mockFilterPipeline.Object,
            _mockSendPipeline.Object,
            _mockRequestReplyManager.Object,
            _mockLogger.Object,
            _mockQueueConfig.Object,
            _mockDispatcher.Object,
            _handlerReferences,
            _mockPipelineConfig.Object,
            _scopeFactory,
            _scopeAccessor,
            consumer);

    // Lifecycle serialization tests.

    [Fact]
    public async Task StartConsumingAsync_ConcurrentWithStop_SerializesState()
    {
        var consumerStarted = new TaskCompletionSource();
        var releaseStart = new TaskCompletionSource();
        var mockConsumer = new Mock<IConsumer>();
        mockConsumer.Setup(c => c.StartConsumingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ConsumerEventHandler>()))
            .Returns(async () =>
            {
                consumerStarted.SetResult();
                await releaseStart.Task;
            });

        await using var bus = CreateBusWithConsumer(mockConsumer.Object);

        var startTask = bus.StartConsumingAsync();
        await consumerStarted.Task;
        var stopTask = bus.StopConsumingAsync();

        // Stop must not complete before Start releases the semaphore
        await Task.Delay(50);
        Assert.False(stopTask.IsCompleted);

        releaseStart.SetResult();
        await startTask;
        await stopTask;

        Assert.False(bus.IsConsuming);
    }

    [Fact]
    public async Task StartConsumingAsync_PreCancelledToken_ThrowsOCE()
    {
        var mockConsumer = new Mock<IConsumer>();
        await using var bus = CreateBusWithConsumer(mockConsumer.Object);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => bus.StartConsumingAsync(cts.Token));
        mockConsumer.Verify(c => c.StartConsumingAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ConsumerEventHandler>()),
            Times.Never);
    }

    [Fact]
    public async Task StopConsumingAsync_WhileStartInFlight_WaitsForStartToComplete()
    {
        var consumerStarted = new TaskCompletionSource();
        var releaseStart = new TaskCompletionSource();
        var startCompleted = new TaskCompletionSource();

        var mockConsumer = new Mock<IConsumer>();
        mockConsumer.Setup(c => c.StartConsumingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ConsumerEventHandler>()))
            .Returns(async () =>
            {
                consumerStarted.SetResult();
                await releaseStart.Task;
            });

        await using var bus = CreateBusWithConsumer(mockConsumer.Object);

        var startTask = Task.Run(async () =>
        {
            await bus.StartConsumingAsync();
            startCompleted.SetResult();
        });

        await consumerStarted.Task;

        // Fire Stop while Start is blocked inside the consumer call
        var stopTask = bus.StopConsumingAsync();

        // Stop is behind Start on the semaphore -- it cannot complete first
        var firstCompleted = await Task.WhenAny(stopTask, startCompleted.Task, Task.Delay(100));
        Assert.NotSame(stopTask, firstCompleted);

        // Release Start; both tasks complete cleanly
        releaseStart.SetResult();
        await startTask;
        await stopTask;
    }

    private object CreateConsumeContextAccessorOrFail()
    {
        var accessorType = typeof(Bus).Assembly.GetType("ServiceConnect.Services.ConsumeContextAccessor");
        Assert.NotNull(accessorType);

        var accessor = Activator.CreateInstance(accessorType!);
        Assert.NotNull(accessor);
        return accessor!;
    }

    private static IDisposable PushConsumeContextOrFail(object accessor, IReadOnlyDictionary<string, object> headers)
    {
        var pushMethod = accessor.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(m => m.Name == "Push" && m.GetParameters().Length == 1);

        Assert.NotNull(pushMethod);

        var scope = pushMethod!.Invoke(accessor, [headers]);
        Assert.IsAssignableFrom<IDisposable>(scope);
        return (IDisposable)scope!;
    }

    private Bus CreateBusWithTimeoutStoreAndAccessorOrFail(ITimeoutStore timeoutStore, object accessor)
    {
        var constructor = typeof(Bus)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(ctor => ctor.GetParameters().Any(p => p.Name == "consumeContextAccessor" && p.ParameterType.IsAssignableFrom(accessor.GetType())));

        Assert.NotNull(constructor);

        var args = constructor!.GetParameters().Select(parameter => parameter.Name switch
        {
            "serializer" => _mockSerializer.Object,
            "filterPipeline" => _mockFilterPipeline.Object,
            "sendPipeline" => _mockSendPipeline.Object,
            "requestReplyManager" => _mockRequestReplyManager.Object,
            "logger" => _mockLogger.Object,
            "queueConfig" => _mockQueueConfig.Object,
            "dispatcher" => _mockDispatcher.Object,
            "handlerReferences" => _handlerReferences,
            "pipelineConfig" => _mockPipelineConfig.Object,
            "scopeFactory" => _scopeFactory,
            "scopeAccessor" => _scopeAccessor,
            "consumer" => null,
            "producer" => null,
            "timeoutStore" => timeoutStore,
            "consumeContextAccessor" => accessor,
            "busConfig" => null,
            "timeProvider" => null,
            _ => throw new InvalidOperationException($"Unexpected Bus constructor parameter '{parameter.Name}'.")
        }).ToArray();

        return (Bus)constructor.Invoke(args);
    }

    [Fact]
    public async Task ConcurrentStartAndDispose_DoesNotThrowSemaphoreDisposedException()
    {
        int iterations = 100;
        int unexpected = 0;
        for (int i = 0; i < iterations; i++)
        {
            var mockConsumer = new Mock<IConsumer>();
            mockConsumer.Setup(x => x.StartConsumingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<ConsumerEventHandler>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var bus = new Bus(
                _mockSerializer.Object,
                _mockFilterPipeline.Object,
                _mockSendPipeline.Object,
                _mockRequestReplyManager.Object,
                _mockLogger.Object,
                _mockQueueConfig.Object,
                _mockDispatcher.Object,
                _handlerReferences,
                _mockPipelineConfig.Object,
                _scopeFactory,
                _scopeAccessor,
                mockConsumer.Object);

            using var barrier = new Barrier(2);

            var startTask = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                try { await bus.StartConsumingAsync(); }
                catch (ObjectDisposedException ode) when (ode.ObjectName == typeof(Bus).FullName) { /* expected */ }
                catch (ObjectDisposedException) { Interlocked.Increment(ref unexpected); }
                catch (InvalidOperationException) { /* race-acceptable */ }
            });
            var disposeTask = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await bus.DisposeAsync();
            });

            await Task.WhenAll(startTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(0, unexpected);
    }
}

file sealed class MarkerProbe
{
}
