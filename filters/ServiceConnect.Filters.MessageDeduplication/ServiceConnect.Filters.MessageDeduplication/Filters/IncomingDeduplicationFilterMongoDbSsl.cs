using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class IncomingDeduplicationFilterMongoDbSsl : IFilter
    {
        private static readonly Lazy<IncomingFilter> _incomingFilter = new(() =>
            new IncomingFilter(new MessageDeduplicationPersistorMongoDbSsl()));

        public bool Process(Envelope envelope)
        {
            return _incomingFilter.Value.Process(envelope);
        }

        public IBus Bus { get; set; }
    }
}
