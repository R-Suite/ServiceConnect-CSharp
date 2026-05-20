using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns.Handlers;

/// <summary>
/// Receives <see cref="WorkItem"/> on either bus, records the handler arrival into
/// <see cref="FlowAccounting"/>, signals the rendezvous registry, and bumps a per-handler
/// counter so the driver can verify that more than one distinct handler instance
/// observed at least one message in the batch.
/// </summary>
/// <remarks>
/// <para>
/// Two <c>IMessageHandler&lt;WorkItem&gt;</c> registrations exist per bus, distinguished
/// by their constructor-injected <paramref name="handlerTag"/>. The framework's dispatcher
/// resolves every matching service via <c>GetServices</c>, so each delivery fans out to
/// both handler instances. The driver's success criterion is "at least two distinct
/// handlers got at least one message" — which holds even when both fire for every
/// message — so the assertion remains meaningful without coupling to a competing-queue
/// topology that the harness's single-process bus pair cannot model directly.
/// </para>
/// <para>
/// Header values arrive as <see cref="object"/> because the transport may deliver them
/// as either <see cref="string"/> (in-process / serialiser fast-path) or
/// <see cref="byte"/>[] (RabbitMQ wire format). <see cref="HeaderDecoder.Decode"/>
/// normalises both shapes; a raw <c>is string</c> pattern check would miss the wire
/// form and silently skip every flow on the live broker.
/// </para>
/// </remarks>
public sealed class WorkItemHandler(
    string handlerTag,
    string busTag,
    FlowAccounting accounting,
    PerHandlerSignal signals,
    WorkItemCounters counters,
    MessageLedger ledger,
    IChaosClock chaosClock)
    : IMessageHandler<WorkItem>
{
    public Task HandleAsync(WorkItem message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        if (context.Headers.TryGetValue(StressHeaders.FlowId, out var raw)
            && HeaderDecoder.Decode(raw) is { } flowIdStr
            && Guid.TryParseExact(flowIdStr, "N", out var flowId))
        {
            accounting.RecordHandled(flowId);
            signals.Signal(flowId, busTag, context);
            counters.Hits.AddOrUpdate($"{busTag}:{handlerTag}", 1, (_, n) => n + 1);
            // WorkItemHandler is registered twice per bus (h1 + h2 tags) so the framework
            // dispatches every WorkItem to both instances; each instance records its own
            // consume row. The analyzer's PerMessageRedeliveries counter will reflect this
            // fan-out — not real broker redelivery — during competing-consumers runs.
            LedgerHandlerHelpers.RecordLedgerConsume(message, context, "competing-consumers", busTag, flowId, ledger, chaosClock);
        }
        return Task.CompletedTask;
    }
}
