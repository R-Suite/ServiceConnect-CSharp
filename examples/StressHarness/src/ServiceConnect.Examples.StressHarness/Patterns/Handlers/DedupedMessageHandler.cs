using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Examples.StressHarness.Patterns.Middleware;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns.Handlers;

/// <summary>
/// Receives <see cref="DedupedMessage"/> on either bus, appends the <c>"handler"</c>
/// marker into the shared <see cref="MiddlewareTrail"/>, records the arrival into
/// <see cref="FlowAccounting"/>, and signals the rendezvous registry.
/// </summary>
/// <remarks>
/// <para>
/// The handler sits between <see cref="StressProcessingMiddleware"/>'s enter and
/// exit markers and between the BeforeConsuming / OnConsumedSuccessfully filters.
/// The driver asserts the receiver trail captures all five stages in order.
/// </para>
/// <para>
/// Header values arrive as <see cref="object"/> because the transport may deliver
/// them as either <see cref="string"/> (in-process / serialiser fast-path) or
/// <see cref="byte"/>[] (RabbitMQ wire format). <see cref="HeaderDecoder.Decode"/>
/// normalises both shapes; a raw <c>is string</c> check would miss the wire form
/// and silently skip every flow on the live broker.
/// </para>
/// </remarks>
public sealed class DedupedMessageHandler(
    string busTag,
    FlowAccounting accounting,
    PerHandlerSignal signals,
    MiddlewareTrail trail)
    : IMessageHandler<DedupedMessage>
{
    public Task HandleAsync(DedupedMessage message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        if (context.Headers.TryGetValue(StressHeaders.FlowId, out var raw)
            && HeaderDecoder.Decode(raw) is { } flowIdStr
            && Guid.TryParseExact(flowIdStr, "N", out var flowId))
        {
            // Trail append must precede the signal so the driver, which only
            // releases its await after the on-success filter has run, never
            // observes a missing handler marker. The on-success filter runs
            // strictly after the handler returns, so by the time the driver
            // wakes the full five-stage trail is committed.
            trail.Record(flowId, "handler");
            accounting.RecordHandled(flowId);
            signals.Signal(flowId, busTag, context);
        }
        return Task.CompletedTask;
    }
}
