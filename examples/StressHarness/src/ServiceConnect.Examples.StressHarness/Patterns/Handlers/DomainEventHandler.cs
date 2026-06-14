using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns.Handlers;

/// <summary>
/// Receives any <see cref="DomainEvent"/> on either bus, records the handler arrival
/// into <see cref="FlowAccounting"/>, and signals the awaiting driver. A single
/// registration of <c>IMessageHandler&lt;DomainEvent&gt;</c> catches both derived
/// concrete types — the dispatcher walks up the message type hierarchy to
/// <see cref="DomainEvent"/> and resolves the handler from there.
/// </summary>
/// <remarks>
/// <para>
/// Each derived event publishes through its own type-derived fanout exchange, so a
/// publish from one bus is delivered to BOTH the publisher's queue (echo) and the
/// receiver's queue (the cross-tenant fan-out the driver awaits). The handler
/// suppresses the local-bus echo by comparing the <c>OriginBus</c> header to its own
/// <c>busTag</c>, leaving exactly two handler invocations per flow on the receiver
/// (one per derived event published under the same flow id).
/// </para>
/// <para>
/// Header values arrive as <see cref="object"/> because the transport may deliver them
/// as either <see cref="string"/> (in-process / serialiser fast-path) or
/// <see cref="byte"/>[] (RabbitMQ wire format). <see cref="HeaderDecoder.Decode"/>
/// normalises both shapes; a raw <c>is string</c> pattern check would miss the wire
/// form and silently skip every flow on the live broker.
/// </para>
/// </remarks>
public sealed class DomainEventHandler(string busTag, FlowAccounting accounting, PerHandlerSignal signals, MessageLedger ledger, IChaosClock chaosClock)
    : IMessageHandler<DomainEvent>
{
    public Task HandleAsync(DomainEvent message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        if (context.Headers.TryGetValue(StressHeaders.OriginBus, out var rawOrigin)
            && HeaderDecoder.Decode(rawOrigin) is { } originBus
            && string.Equals(originBus, busTag, StringComparison.Ordinal))
        {
            // Echo back to the publishing bus — the driver only accounts for the
            // cross-tenant deliveries, so suppress the local-bus copies entirely.
            return Task.CompletedTask;
        }

        if (context.Headers.TryGetValue(StressHeaders.FlowId, out var raw)
            && HeaderDecoder.Decode(raw) is { } flowIdStr
            && Guid.TryParseExact(flowIdStr, "N", out var flowId))
        {
            accounting.RecordHandled(flowId);
            signals.Signal(flowId, busTag, context);
            LedgerHandlerHelpers.RecordLedgerConsume(message, context, "polymorphic", busTag, flowId, ledger, chaosClock);
        }
        return Task.CompletedTask;
    }
}
