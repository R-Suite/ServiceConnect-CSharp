using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns.Handlers;

/// <summary>
/// Receives <see cref="SlipOrder"/> on either bus, records the handler arrival into
/// <see cref="FlowAccounting"/>, appends the responding bus tag to the per-flow
/// <see cref="SlipTrail"/>, and returns. The framework's slip-forwarder runs after
/// the handler completes and dispatches the message to the next destination in the
/// envelope's <c>RoutingSlip</c> header — the handler itself never invokes
/// <see cref="IBus.RouteAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two hops cross both buses in opposite orders depending on the slip origin:
/// alpha-issued slip visits alpha-then-beta, beta-issued slip visits beta-then-alpha.
/// The trail's append order reflects the actual hop order, so the driver's
/// assertion confirms the slip honoured the destination list rather than the
/// framework reordering hops or skipping a destination.
/// </para>
/// <para>
/// Flow correlation is read from <see cref="Message.CorrelationId"/> rather than
/// from <c>StressHeaders.FlowId</c> because <see cref="IBus.RouteAsync"/> does not
/// accept a <c>SendOptions</c> overload — there is no caller-visible path to attach
/// custom headers to a routed message. The driver constructs the <see cref="SlipOrder"/>
/// with the flow id on the message's correlation id, matching the convention the
/// aggregator driver uses for the same reason (its <c>ExecuteAsync</c> has no
/// access to per-message headers).
/// </para>
/// </remarks>
public sealed class SlipOrderHandler(string busTag, FlowAccounting accounting, PerHandlerSignal signals, SlipTrail trail, MessageLedger ledger, IChaosClock chaosClock)
    : IMessageHandler<SlipOrder>
{
    public Task HandleAsync(SlipOrder message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        var flowId = message.CorrelationId;
        accounting.RecordHandled(flowId);
        // Signal the rendezvous for accounting parity; the driver waits on the
        // trail length rather than this signal because PerHandlerSignal is one-shot
        // per flow id and the slip fires the handler twice (once per hop). The
        // first hop wins the await; the second hop's signal is a no-op because the
        // TCS is already completed.
        signals.Signal(flowId, busTag, context);
        trail.Record(flowId, busTag);
        // RouteAsync does not forward caller-controlled headers, so X-Stress-MessageId
        // is absent from inbound context. The helper falls back to message.CorrelationId
        // as the ledger key; the publish side records under the same key for this path.
        LedgerHandlerHelpers.RecordLedgerConsume(message, context, "routing-slip", busTag, flowId, ledger, chaosClock);
        return Task.CompletedTask;
    }
}
