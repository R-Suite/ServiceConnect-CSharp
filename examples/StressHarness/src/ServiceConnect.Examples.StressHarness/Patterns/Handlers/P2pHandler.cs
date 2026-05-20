using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns.Handlers;

/// <summary>
/// Receives <see cref="P2pPing"/> on either bus, records the handler arrival into
/// <see cref="FlowAccounting"/>, and signals the awaiting driver through
/// <see cref="PerHandlerSignal"/>.
/// </summary>
/// <remarks>
/// <para>
/// The same handler class is instantiated on both alpha and beta buses through a factory
/// registration that closes over the bus tag — so a single registry knows which bus saw
/// each flow without inspecting headers. The handler itself ignores message content;
/// the harness's payload integrity check happens in the driver after the rendezvous.
/// </para>
/// <para>
/// Header values arrive as <see cref="object"/> because the transport may deliver them
/// as either <see cref="string"/> (in-process / serialiser fast-path) or
/// <see cref="byte[]"/> (RabbitMQ wire format). <see cref="HeaderDecoder.Decode"/>
/// normalises both shapes — a raw <c>is string</c> pattern check would miss the wire
/// form and silently skip every flow on the live broker.
/// </para>
/// </remarks>
public sealed class P2pHandler(string busTag, FlowAccounting accounting, PerHandlerSignal signals, MessageLedger ledger, IChaosClock chaosClock)
    : IMessageHandler<P2pPing>
{
    public Task HandleAsync(P2pPing message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        if (context.Headers.TryGetValue(StressHeaders.FlowId, out var raw)
            && HeaderDecoder.Decode(raw) is { } flowIdStr
            && Guid.TryParseExact(flowIdStr, "N", out var flowId))
        {
            accounting.RecordHandled(flowId);
            signals.Signal(flowId, busTag, context);
            LedgerHandlerHelpers.RecordLedgerConsume(message, context, "p2p", busTag, flowId, ledger, chaosClock);
        }
        return Task.CompletedTask;
    }
}
