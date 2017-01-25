using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class IncomingDeduplicationFilterMongoDbSsl : IFilter
    {
        public bool Process(Envelope envelope)
        {
            var incomingFilter = new IncomingFilter(new MessageDeduplicationPersistorMongoDbSsl());

            return incomingFilter.Process(envelope);
        }

        public IBus Bus { get; set; }
    }
}
