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

        public IBus Bus { get; set; } = null!;

        public IncomingDeduplicationFilter(IMessageDeduplicationPersistor persistor)
        {
            _persistor = persistor ?? throw new ArgumentNullException(nameof(persistor));
        }

        public async Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!envelope.Headers.ContainsKey("Redelivered"))
                return true;

            if (!bool.TryParse(HeaderDecoder.Decode(envelope.Headers["Redelivered"]), out var redelivered) || !redelivered)
                return true;

            var messageId = new Guid(HeaderDecoder.Decode(envelope.Headers["MessageId"]) ?? string.Empty);
            var exists = await _persistor.GetMessageExistsAsync(messageId, cancellationToken).ConfigureAwait(false);

            return !exists;
        }
    }
}
