using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

/// <summary>
/// Covers ForwardRoutingSlipAsync behaviour via ProcessAsync — the forward method is
/// private static, so all assertions flow through the public dispatch path.
/// </summary>
public class HandlerProcessorRoutingSlipTests
{
    private static readonly IBusConfiguration RoutingSlipBusConfig = new BusConfiguration
    {
        EnableRoutingSlipProcessing = true
    };

    // Minimal queue config without any explicit queue mappings — used to confirm
    // that cross-service destinations are not rejected when IsKnownQueue is absent.
    private static readonly IQueueConfiguration MinimalQueueConfig = new QueueConfiguration
    {
        QueueName = "local-service-q",
        ErrorQueueName = "errors",
        AuditQueueName = "audit"
    };

    private static ConsumeScopeAccessor NewScope(IServiceProvider sp)
    {
        var accessor = new ConsumeScopeAccessor();
        accessor.Push(sp);
        return accessor;
    }

    private static MessageHandlerRegistry BuildRegistry(params Type[] messageTypes)
    {
        var refs = messageTypes
            .Select(mt => new HandlerReference { MessageType = mt, HandlerType = typeof(SlipTestHandler) })
            .ToList();
        return new MessageHandlerRegistry(refs, NullLogger<MessageHandlerRegistry>.Instance);
    }

    /// <summary>
    /// A well-formed cross-service queue name that is not in the local queueConfig must be
    /// allowed; IBus.RouteAsync is called with that destination. A strict IsKnownQueue check
    /// would throw InvalidOperationException and block legitimate cross-service routing slips.
    /// </summary>
    [Fact]
    public async Task ForwardRoutingSlip_DestinationNotInLocalConfig_DoesNotThrow()
    {
        var handler = new SlipTestHandler();
        var mockBus = new Mock<IBus>();
        mockBus.Setup(b => b.RouteAsync(It.IsAny<SlipTestMsg>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<SlipTestMsg>>(handler);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        // MinimalQueueConfig has no mapping for "remote-service-q" — must still be accepted.
        var processor = new HandlerProcessor(
            BuildRegistry(typeof(SlipTestMsg)),
            NewScope(provider),
            new Lazy<IBus>(() => mockBus.Object),
            RoutingSlipBusConfig,
            MinimalQueueConfig,
            new ConsumeContextPool(),
            new ConsumeContextAccessor(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HandlerProcessor>.Instance);

        var msg = new SlipTestMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.RoutingSlip] = "remote-service-q"
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        // No throw — the destination passes format validation.
        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(SlipTestMsg), msg, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
        mockBus.Verify(
            b => b.RouteAsync(msg, It.Is<IReadOnlyList<string>>(d => d.Count == 1 && d[0] == "remote-service-q"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Cross-service routing slip with multiple destinations in a single header —
    /// none of the destinations are in the local queue config, but all are well-formed.
    /// All must be forwarded in order.
    /// </summary>
    [Fact]
    public async Task ForwardRoutingSlip_MultipleDestinationsNotInLocalConfig_AllForwarded()
    {
        var handler = new SlipTestHandler();
        var mockBus = new Mock<IBus>();
        mockBus.Setup(b => b.RouteAsync(It.IsAny<SlipTestMsg>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
               .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<SlipTestMsg>>(handler);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(
            BuildRegistry(typeof(SlipTestMsg)),
            NewScope(provider),
            new Lazy<IBus>(() => mockBus.Object),
            RoutingSlipBusConfig,
            MinimalQueueConfig,
            new ConsumeContextPool(),
            new ConsumeContextAccessor(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HandlerProcessor>.Instance);

        var msg = new SlipTestMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.RoutingSlip] = "service-b-q, service-c-q"
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        await processor.ProcessAsync(new byte[] { 1 }, typeof(SlipTestMsg), msg, headers, envelope);

        mockBus.Verify(
            b => b.RouteAsync(msg, It.Is<IReadOnlyList<string>>(d => d.Count == 2 && d[0] == "service-b-q" && d[1] == "service-c-q"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// IsValidRoutingSlipDestination is the remaining gate once the IsKnownQueue check is
    /// relaxed. Malformed destinations containing AMQP wildcards or control characters must
    /// still be rejected with InvalidOperationException.
    /// </summary>
    /// <remarks>
    /// A whitespace-only header value is caught by the outer IsNullOrWhiteSpace guard
    /// (treated as "no slip") and is covered by ForwardRoutingSlip_WhitespaceOnlyHeaderValue_TreatedAsNoSlip.
    /// Empty-string tokens inside a multi-part slip are dropped by Split(RemoveEmptyEntries)
    /// before reaching the validator.
    /// </remarks>
    [Theory]
    [InlineData("has*wildcard")]
    [InlineData("has#wildcard")]
    [InlineData("has\nnewline")]
    [InlineData("has\rnewline")]
    [InlineData("has\ttab")]
    public async Task ForwardRoutingSlip_MalformedDestination_LogsAndReturnsHandled(string badDestination)
    {
        var handler = new SlipTestHandler();
        var mockBus = new Mock<IBus>();

        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<SlipTestMsg>>(handler);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(
            BuildRegistry(typeof(SlipTestMsg)),
            NewScope(provider),
            new Lazy<IBus>(() => mockBus.Object),
            RoutingSlipBusConfig,
            MinimalQueueConfig,
            new ConsumeContextPool(),
            new ConsumeContextAccessor(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HandlerProcessor>.Instance);

        var msg = new SlipTestMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.RoutingSlip] = badDestination
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        // Handlers already succeeded; the malformed-destination validation throws
        // InvalidOperationException inside ForwardRoutingSlipAsync, which is caught
        // and logged so the message is acked rather than re-running on retry.
        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(SlipTestMsg), msg, headers, envelope);
        Assert.Equal(ProcessResult.Handled, result);
        mockBus.Verify(
            b => b.RouteAsync(It.IsAny<SlipTestMsg>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A whitespace-only RoutingSlip header value is treated as "no slip present" —
    /// the outer IsNullOrWhiteSpace guard returns early before reaching the per-token
    /// validation loop, so no exception is thrown and RouteAsync is never called.
    /// </summary>
    [Fact]
    public async Task ForwardRoutingSlip_WhitespaceOnlyHeaderValue_TreatedAsNoSlip()
    {
        var handler = new SlipTestHandler();
        var mockBus = new Mock<IBus>();

        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<SlipTestMsg>>(handler);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(
            BuildRegistry(typeof(SlipTestMsg)),
            NewScope(provider),
            new Lazy<IBus>(() => mockBus.Object),
            RoutingSlipBusConfig,
            MinimalQueueConfig,
            new ConsumeContextPool(),
            new ConsumeContextAccessor(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HandlerProcessor>.Instance);

        var msg = new SlipTestMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.RoutingSlip] = "   "
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        // IsNullOrWhiteSpace("   ") → true → early return → no RouteAsync call, no throw.
        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(SlipTestMsg), msg, headers, envelope);

        Assert.Equal(ProcessResult.Handled, result);
        mockBus.Verify(b => b.RouteAsync(It.IsAny<SlipTestMsg>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// When a handler throws, AggregateException propagates before ForwardRoutingSlipAsync
    /// runs. IBus.RouteAsync must never be called — the slip is dropped from the in-flight
    /// forward path, but the envelope RoutingSlip header is untouched (not tested here
    /// because that is a serialisation concern, not a processor concern).
    /// </summary>
    [Fact]
    public async Task ForwardRoutingSlip_HandlerThrows_SlipNotForwarded()
    {
        var mockBus = new Mock<IBus>();

        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<SlipTestMsg>>(new ThrowingSlipHandler("boom"));
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(
            BuildRegistry(typeof(SlipTestMsg)),
            NewScope(provider),
            new Lazy<IBus>(() => mockBus.Object),
            RoutingSlipBusConfig,
            MinimalQueueConfig,
            new ConsumeContextPool(),
            new ConsumeContextAccessor(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HandlerProcessor>.Instance);

        var msg = new SlipTestMsg(Guid.NewGuid());
        // Destination is well-formed and would normally be forwarded.
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.RoutingSlip] = "remote-service-q"
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        await Assert.ThrowsAsync<AggregateException>(
            () => processor.ProcessAsync(new byte[] { 1 }, typeof(SlipTestMsg), msg, headers, envelope));

        // The AggregateException propagates before ForwardRoutingSlipAsync is reached.
        mockBus.Verify(b => b.RouteAsync(It.IsAny<SlipTestMsg>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// When handlers succeed and the slip-forward then fails, ProcessAsync must return
    /// Handled — the dispatcher acks the broker and the message does not re-enter the
    /// retry queue. Without this, the slip-forward failure surfaces as Success=false,
    /// the broker redelivers the message, and the handlers run again on every retry
    /// until the budget is exhausted — duplicating side effects that already succeeded.
    /// </summary>
    [Fact]
    public async Task ForwardRoutingSlip_HandlerSucceedsButForwardFails_ReturnsHandledAndSwallowsFailure()
    {
        var handler = new SlipTestHandler();
        var mockBus = new Mock<IBus>();
        // RouteAsync throws a transient transport error after handlers succeed.
        mockBus.Setup(b => b.RouteAsync(It.IsAny<SlipTestMsg>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("transient broker disconnect"));

        var services = new ServiceCollection();
        services.AddSingleton<IMessageHandler<SlipTestMsg>>(handler);
        services.AddSingleton(mockBus.Object);
        var provider = services.BuildServiceProvider();

        var processor = new HandlerProcessor(
            BuildRegistry(typeof(SlipTestMsg)),
            NewScope(provider),
            new Lazy<IBus>(() => mockBus.Object),
            RoutingSlipBusConfig,
            MinimalQueueConfig,
            new ConsumeContextPool(),
            new ConsumeContextAccessor(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HandlerProcessor>.Instance);

        var msg = new SlipTestMsg(Guid.NewGuid());
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.RoutingSlip] = "remote-service-q"
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 1 } };

        // The slip-forward throws but ProcessAsync must NOT propagate it: the handler
        // already ran successfully and a re-run on retry would duplicate side effects.
        var result = await processor.ProcessAsync(new byte[] { 1 }, typeof(SlipTestMsg), msg, headers, envelope);
        Assert.Equal(ProcessResult.Handled, result);

        // RouteAsync was actually attempted (proving the failure path was exercised).
        mockBus.Verify(
            b => b.RouteAsync(It.IsAny<SlipTestMsg>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

}

file class SlipTestMsg(Guid correlationId) : Message(correlationId);

file sealed class SlipTestHandler : IMessageHandler<SlipTestMsg>
{
    public Task HandleAsync(SlipTestMsg message, IConsumeContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

file sealed class ThrowingSlipHandler(string errorMessage) : IMessageHandler<SlipTestMsg>
{
    public Task HandleAsync(SlipTestMsg message, IConsumeContext context, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(errorMessage);
}
