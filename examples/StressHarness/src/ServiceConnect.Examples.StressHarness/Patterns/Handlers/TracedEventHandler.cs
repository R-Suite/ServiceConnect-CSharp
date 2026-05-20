using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns.Handlers;

/// <summary>
/// Receives <see cref="TracedEvent"/> on either bus, records the handler arrival
/// into <see cref="FlowAccounting"/>, and signals the awaiting driver. The
/// handler's only job is to fire the rendezvous — the driver's assertion lives
/// in the global <c>TelemetryObservations</c> bag, which the framework's
/// telemetry middleware populates around dispatch without any handler co-operation.
/// </summary>
/// <remarks>
/// <para>
/// Telemetry uses a type-derived fanout exchange shared across both buses (same
/// shape as the pub/sub driver). Each bus binds its own queue to that exchange,
/// so a publish from alpha is delivered to BOTH the alpha queue (echo to the
/// sender) and the beta queue (the cross-tenant fan-out the driver awaits). The
/// handler filters out the echo by comparing the <c>OriginBus</c> header to its
/// own <c>busTag</c> — only the cross-bus delivery records and signals, matching
/// the driver's <c>expectedHandlerInvocations: 1</c> accounting.
/// </para>
/// <para>
/// Header values arrive as <see cref="object"/> because the transport may deliver
/// them as either <see cref="string"/> (in-process / serialiser fast-path) or
/// <see cref="byte"/>[] (RabbitMQ wire format). <see cref="HeaderDecoder.Decode"/>
/// normalises both shapes; a raw <c>is string</c> check would miss the wire form
/// and silently skip every flow on the live broker.
/// </para>
/// </remarks>
public sealed class TracedEventHandler(string busTag, FlowAccounting accounting, PerHandlerSignal signals, MessageLedger ledger, IChaosClock chaosClock)
    : IMessageHandler<TracedEvent>
{
    public Task HandleAsync(TracedEvent message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        if (context.Headers.TryGetValue(StressHeaders.OriginBus, out var rawOrigin)
            && HeaderDecoder.Decode(rawOrigin) is { } originBus
            && string.Equals(originBus, busTag, StringComparison.Ordinal))
        {
            // Echo back to the publishing bus — the driver only accounts for the
            // cross-tenant delivery, so suppress the local-bus copy entirely.
            return Task.CompletedTask;
        }

        if (context.Headers.TryGetValue(StressHeaders.FlowId, out var raw)
            && HeaderDecoder.Decode(raw) is { } flowIdStr
            && Guid.TryParseExact(flowIdStr, "N", out var flowId))
        {
            accounting.RecordHandled(flowId);
            signals.Signal(flowId, busTag, context);
            LedgerHandlerHelpers.RecordLedgerConsume(message, context, "telemetry", busTag, flowId, ledger, chaosClock);
        }
        return Task.CompletedTask;
    }
}
