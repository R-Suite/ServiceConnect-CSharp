using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class OutgoingDeduplicationFilterMongoDbSsl : IFilter
    {
        private static OutgoingFilter _outgoingFilter;

        public bool Process(Envelope envelope)
        {
            if (null == _outgoingFilter)
            {
                _outgoingFilter = new OutgoingFilter(new MessageDeduplicationPersistorMongoDbSsl());
            }

            return _outgoingFilter.Process(envelope);
        }

        public IBus Bus { get; set; }
    }
}
