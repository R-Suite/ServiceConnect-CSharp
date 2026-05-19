using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Examples.StressHarness.Patterns.Filters;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns.Handlers;

/// <summary>
/// Receives <see cref="FilteredMessage"/> on either bus, appends a <c>"handler"</c>
/// marker into the shared <see cref="FilterTrail"/>, records the arrival into
/// <see cref="FlowAccounting"/>, and signals the rendezvous registry.
/// </summary>
/// <remarks>
/// <para>
/// The companion <see cref="StressTrailFilter"/> runs at the BeforeConsuming stage
/// and appends <c>"filter"</c> ahead of this handler. The driver asserts the trail
/// captures both markers in order on the receiver side, demonstrating the framework
/// dispatches inbound filters strictly before the handler.
/// </para>
/// <para>
/// Header values arrive as <see cref="object"/> because the transport may deliver
/// them as either <see cref="string"/> (in-process / serialiser fast-path) or
/// <see cref="byte"/>[] (RabbitMQ wire format). <see cref="HeaderDecoder.Decode"/>
/// normalises both shapes; a raw <c>is string</c> check would miss the wire form
/// and silently skip every flow on the live broker.
/// </para>
/// </remarks>
public sealed class FilteredMessageHandler(
    string busTag,
    FlowAccounting accounting,
    PerHandlerSignal signals,
    FilterTrail trail)
    : IMessageHandler<FilteredMessage>
{
    public Task HandleAsync(FilteredMessage message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        if (context.Headers.TryGetValue(StressHeaders.FlowId, out var raw)
            && HeaderDecoder.Decode(raw) is { } flowIdStr
            && Guid.TryParseExact(flowIdStr, "N", out var flowId))
        {
            // Trail append must precede the signal so the driver, which only releases
            // its await after Signal fires, observes the handler marker on read-back.
            trail.Record(flowId, "handler");
            accounting.RecordHandled(flowId);
            signals.Signal(flowId, busTag, context);
        }
        return Task.CompletedTask;
    }
}
