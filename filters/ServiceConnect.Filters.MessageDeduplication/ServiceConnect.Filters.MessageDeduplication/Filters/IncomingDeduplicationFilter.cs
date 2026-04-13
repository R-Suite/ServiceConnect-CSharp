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
        private static IMessageDeduplicationPersistor? _overridePersistor;
        private static readonly Lazy<IMessageDeduplicationPersistor> _defaultPersistor = new(() =>
            PersistorFactory.Create(DeduplicationFilterSettings.Instance.PersistorType));

        public IBus Bus { get; set; } = null!;

        private static IMessageDeduplicationPersistor Persistor => _overridePersistor ?? _defaultPersistor.Value;

        internal static void OverridePersistorForTesting(IMessageDeduplicationPersistor persistor) => _overridePersistor = persistor;

        public async Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // RabbitMQ guarantees a first-delivery has never been seen before; skip the check.
            // https://www.rabbitmq.com/reliability.html
            if (!envelope.Headers.ContainsKey("Redelivered"))
                return true;

            if (!bool.TryParse(HeaderDecoder.Decode(envelope.Headers["Redelivered"]), out var redelivered) || !redelivered)
                return true;

            var messageId = new Guid(HeaderDecoder.Decode(envelope.Headers["MessageId"]) ?? string.Empty);
            var exists = await Persistor.GetMessageExistsAsync(messageId, cancellationToken).ConfigureAwait(false);

            // exists = duplicate -> block (false). Otherwise continue (true).
            return !exists;
        }
    }
}
