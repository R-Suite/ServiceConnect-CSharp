using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns.Handlers;

/// <summary>
/// Receives <see cref="SearchRequest"/> on either bus, records the handler arrival
/// into <see cref="FlowAccounting"/>, signals the rendezvous registry, then replies
/// with a <see cref="SearchResponse"/> tagged with the bus identity through
/// <see cref="IConsumeContext.ReplyAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ServiceConnect.Interfaces.IBus.PublishRequestAsync"/> dispatches the
/// request to every queue bound to the <see cref="SearchRequest"/> type-fanout
/// exchange. With one registration per bus, a publish from alpha fans to both
/// alpha's and beta's queues; each handler replies once and the requester's
/// callback collects two replies — the count matched against
/// <see cref="ServiceConnect.Interfaces.Options.RequestOptions.ExpectedReplyCount"/>.
/// </para>
/// <para>
/// <see cref="SearchResponse.CatalogName"/> is set to the handler's bus tag so the
/// driver can assert both alpha and beta produced a reply (the fanout reached both
/// subscribers), distinct from a stuck pattern where one bus replies twice.
/// </para>
/// <para>
/// Header values arrive as <see cref="object"/> because the transport may deliver
/// them as either <see cref="string"/> (in-process / serialiser fast-path) or
/// <see cref="byte"/>[] (RabbitMQ wire format). <see cref="HeaderDecoder.Decode"/>
/// normalises both shapes; a raw <c>is string</c> pattern check would miss the wire
/// form and silently skip every flow on the live broker.
/// </para>
/// </remarks>
public sealed class SearchRequestHandler(string busTag, FlowAccounting accounting, PerHandlerSignal signals, MessageLedger ledger, IChaosClock chaosClock)
    : IMessageHandler<SearchRequest>
{
    public async Task HandleAsync(SearchRequest message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        if (context.Headers.TryGetValue(StressHeaders.FlowId, out var raw)
            && HeaderDecoder.Decode(raw) is { } flowIdStr
            && Guid.TryParseExact(flowIdStr, "N", out var flowId))
        {
            accounting.RecordHandled(flowId);
            signals.Signal(flowId, busTag, context);
            LedgerHandlerHelpers.RecordLedgerConsume(message, context, "scatter-gather", busTag, flowId, ledger, chaosClock);
        }

        // Reply destination is taken from the incoming envelope's reply-to header
        // by the request-reply manager; no endpoint or routing key is set here.
        // CatalogName carries the responding bus tag so the driver can prove the
        // fanout reached both subscribers by inspecting the distinct values in the
        // collected reply set.
        await context.ReplyAsync(
            new SearchResponse(message.CorrelationId)
            {
                CatalogName = busTag,
                ResultId = message.CorrelationId.ToString("N"),
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
