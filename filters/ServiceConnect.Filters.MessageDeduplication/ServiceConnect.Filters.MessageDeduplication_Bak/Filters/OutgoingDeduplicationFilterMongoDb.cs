using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class OutgoingDeduplicationFilterMongoDb : IFilter
    {
        public bool Process(Envelope envelope)
        {
            var outgoingFilter = new OutgoingFilter(new MessageDeduplicationPersistorMongoDb());

            return outgoingFilter.Process(envelope);
        }

        public IBus Bus { get; set; }
    }
}
