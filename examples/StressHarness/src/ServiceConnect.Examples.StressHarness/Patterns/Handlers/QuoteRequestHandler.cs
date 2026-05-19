using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns.Handlers;

/// <summary>
/// Receives <see cref="QuoteRequest"/> on either bus, records the handler arrival into
/// <see cref="FlowAccounting"/>, signals the rendezvous registry, then replies with a
/// <see cref="QuoteResponse"/> through <see cref="IConsumeContext.ReplyAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// The driver awaits the reply directly through
/// <see cref="ServiceConnect.Interfaces.IBus.SendRequestAsync"/> rather than via
/// <see cref="PerHandlerSignal"/>, but the handler still records and signals for
/// accounting consistency with the other pattern drivers — a missing reply at the
/// driver and a missing signal at the registry have different operator-visible
/// symptoms (timeout vs reconciliation mismatch) and the harness wants both.
/// </para>
/// <para>
/// Header values arrive as <see cref="object"/> because the transport may deliver them
/// as either <see cref="string"/> (in-process / serialiser fast-path) or
/// <see cref="byte"/>[] (RabbitMQ wire format). <see cref="HeaderDecoder.Decode"/>
/// normalises both shapes; a raw <c>is string</c> pattern check would miss the wire
/// form and silently skip every flow on the live broker.
/// </para>
/// </remarks>
public sealed class QuoteRequestHandler(string busTag, FlowAccounting accounting, PerHandlerSignal signals)
    : IMessageHandler<QuoteRequest>
{
    public async Task HandleAsync(QuoteRequest message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        if (context.Headers.TryGetValue(StressHeaders.FlowId, out var raw)
            && HeaderDecoder.Decode(raw) is { } flowIdStr
            && Guid.TryParseExact(flowIdStr, "N", out var flowId))
        {
            accounting.RecordHandled(flowId);
            signals.Signal(flowId, busTag, context);
        }

        // Reply destination is taken from the incoming envelope's reply-to header by
        // the request-reply manager; no endpoint or routing key is set here.
        await context.ReplyAsync(
            new QuoteResponse(message.CorrelationId) { Price = 42.50m },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
