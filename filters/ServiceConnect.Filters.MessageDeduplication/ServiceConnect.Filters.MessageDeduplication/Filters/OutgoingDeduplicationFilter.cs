#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class OutgoingDeduplicationFilter : IFilter
    {
        private readonly IMessageDeduplicationPersistor _persistor;
        private readonly DeduplicationFilterSettings _settings;

        public IBus Bus { get; set; } = null!;

        public OutgoingDeduplicationFilter(
            IMessageDeduplicationPersistor persistor,
            IOptions<DeduplicationFilterSettings> options)
        {
            if (persistor is null) throw new ArgumentNullException(nameof(persistor));
            if (options is null) throw new ArgumentNullException(nameof(options));
            _persistor = persistor;
            _settings = options.Value;
        }

        public async Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var messageId = new Guid(HeaderDecoder.Decode(envelope.Headers["MessageId"]) ?? string.Empty);
            var expiry = DateTime.UtcNow.AddHours(_settings.MsgExpiryHours);

            // Fail-closed: no try/catch. If InsertAsync throws, the send fails and
            // the caller can retry. Silently swallowing would break the dedup guarantee.
            await _persistor.InsertAsync(messageId, expiry, cancellationToken).ConfigureAwait(false);

            return true; // continue pipeline (true = continue, false = block)
        }
    }
}
