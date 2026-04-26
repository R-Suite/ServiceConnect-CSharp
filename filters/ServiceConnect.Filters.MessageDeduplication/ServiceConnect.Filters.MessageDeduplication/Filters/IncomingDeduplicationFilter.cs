#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class IncomingDeduplicationFilter : IFilter
    {
        private readonly IMessageDeduplicationPersistor _persistor;


        public IncomingDeduplicationFilter(IMessageDeduplicationPersistor persistor)
        {
            _persistor = persistor ?? throw new ArgumentNullException(nameof(persistor));
        }

        public async Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!envelope.Headers.TryGetValue("Redelivered", out var redeliveredRaw))
                return FilterAction.Continue;

            // RabbitMQ consumer code writes Redelivered as a raw bool, while other
            // callers may still provide the legacy string/byte[] forms.
            var redelivered = redeliveredRaw is bool boolValue
                ? boolValue
                : bool.TryParse(HeaderDecoder.Decode(redeliveredRaw), out var parsed) && parsed;

            if (!redelivered)
                return FilterAction.Continue;

            // Use TryParse to tolerate malformed MessageId headers rather than throwing
            // FormatException on arbitrary input. Missing or malformed id: let
            // the message through; deduplication cannot apply without a valid key.
            if (!envelope.Headers.TryGetValue("MessageId", out var messageIdRaw) ||
                !Guid.TryParse(HeaderDecoder.Decode(messageIdRaw), out var messageId))
                return FilterAction.Continue;

            var exists = await _persistor.GetMessageExistsAsync(messageId, cancellationToken).ConfigureAwait(false);

            return exists ? FilterAction.Stop : FilterAction.Continue;
        }
    }
}
