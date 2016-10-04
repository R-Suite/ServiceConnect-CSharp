using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class OutgoingDeduplicationFilterInMemory : IFilter
    {
        public bool Process(Envelope envelope)
        {
            var outgoingFilter = new OutgoingFilter(new MessageDeduplicationPersistorInMemory());

            return outgoingFilter.Process(envelope);
        }

        public IBus Bus { get; set; }
    }
}
