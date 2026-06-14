using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.Filters;

/// <summary>
/// BeforeConsuming filter. Consults the persistor; if the MessageId is already
/// recorded, returns Stop so the dispatcher acks-and-drops the redelivery
/// without invoking the handler.
/// </summary>
public sealed class DedupeIncomingFilter(
    IDedupePersistor persistor,
    ILogger<DedupeIncomingFilter> logger) : IFilter
{
    public async Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        if (!envelope.Headers.TryGetValue("MessageId", out var raw) ||
            !Guid.TryParse(HeaderDecoder.Decode(raw), out var messageId))
        {
            // No id, can't dedupe — let the message through.
            return FilterAction.Continue;
        }

        if (await persistor.ContainsAsync(messageId, cancellationToken).ConfigureAwait(false))
        {
            logger.LogInformation("Dedupe: blocking duplicate delivery of MessageId {MessageId}", messageId);
            return FilterAction.Stop;
        }

        return FilterAction.Continue;
    }
}
