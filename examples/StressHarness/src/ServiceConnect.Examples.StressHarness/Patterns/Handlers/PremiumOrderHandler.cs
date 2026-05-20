using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns.Handlers;

/// <summary>
/// Receives <see cref="PremiumOrder"/> on either bus, records the handler arrival into
/// <see cref="FlowAccounting"/>, and signals the awaiting driver. Distinct from
/// <see cref="StandardOrderHandler"/> so the driver can verify that the type-derived
/// fanout exchange routes each variant to exactly its own handler.
/// </summary>
/// <remarks>
/// <para>
/// Pub/sub uses a type-derived fanout exchange shared across both buses. Each bus binds
/// its own queue to that exchange, so a publish from alpha is delivered to BOTH the
/// alpha queue (echo to the sender) and the beta queue (the cross-tenant fan-out the
/// driver awaits). The handler filters out the echo by comparing the <c>OriginBus</c>
/// header to its own <c>busTag</c>, matching the driver's
/// <c>expectedHandlerInvocations: 1</c> bookkeeping.
/// </para>
/// </remarks>
public sealed class PremiumOrderHandler(string busTag, FlowAccounting accounting, PerHandlerSignal signals, MessageLedger ledger, IChaosClock chaosClock)
    : IMessageHandler<PremiumOrder>
{
    public Task HandleAsync(PremiumOrder message, IConsumeContext context, CancellationToken cancellationToken = default)
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
            LedgerHandlerHelpers.RecordLedgerConsume(message, context, "content-based-routing", busTag, flowId, ledger, chaosClock);
        }
        return Task.CompletedTask;
    }
}
