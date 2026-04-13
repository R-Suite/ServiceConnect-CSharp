#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class OutgoingDeduplicationFilter : IFilter
    {
        private static IMessageDeduplicationPersistor? _overridePersistor;
        private static readonly Lazy<IMessageDeduplicationPersistor> _defaultPersistor = new(() =>
            PersistorFactory.Create(DeduplicationFilterSettings.Instance.PersistorType));

        public IBus Bus { get; set; } = null!;

        private static IMessageDeduplicationPersistor Persistor => _overridePersistor ?? _defaultPersistor.Value;

        // Internal test seam — removed in Task 4 when DI takes over.
        internal static void OverridePersistorForTesting(IMessageDeduplicationPersistor persistor) => _overridePersistor = persistor;

        public async Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var messageId = new Guid(HeaderDecoder.Decode(envelope.Headers["MessageId"]) ?? string.Empty);
            var expiry = DateTime.UtcNow.AddHours(DeduplicationFilterSettings.Instance.MsgExpiryHours);

            // Fail-closed: no try/catch. If InsertAsync throws, the send fails and
            // the caller can retry. Silently swallowing would break the dedup guarantee.
            await Persistor.InsertAsync(messageId, expiry, cancellationToken).ConfigureAwait(false);

            return true; // continue pipeline (true = continue, false = block)
        }
    }
}
