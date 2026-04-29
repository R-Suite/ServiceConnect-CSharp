using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.Filters;

/// <summary>
/// OnConsumedSuccessfully filter. Only fires after the handler has completed
/// successfully — failures and unhandled messages skip this stage. Records the
/// MessageId atomically; if the persistor reports the id was already present
/// (e.g. two concurrent deliveries raced past the BeforeConsuming check), the
/// filter throws so the dispatcher returns Success=false and the broker
/// redelivers, letting the next attempt's BeforeConsuming filter block.
/// </summary>
public sealed class DedupeOnSuccessFilter(
    IDedupePersistor persistor,
    ILogger<DedupeOnSuccessFilter> logger) : IFilter
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public async Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        if (!envelope.Headers.TryGetValue("MessageId", out var raw) ||
            !Guid.TryParse(HeaderDecoder.Decode(raw), out var messageId))
        {
            return FilterAction.Continue;
        }

        var inserted = await persistor.TryInsertAsync(messageId, DateTime.UtcNow + Retention, cancellationToken)
            .ConfigureAwait(false);

        if (!inserted)
        {
            // BeforeConsuming and OnSuccess raced. Throwing here makes the dispatcher
            // return Success=false → broker redelivers → next attempt's BeforeConsuming
            // filter sees the id and returns Stop.
            throw new InvalidOperationException(
                $"Concurrent delivery of MessageId {messageId} already recorded; redelivery will dedupe.");
        }

        logger.LogDebug("Dedupe: recorded MessageId {MessageId}", messageId);
        return FilterAction.Continue;
    }
}
