using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class OutgoingDeduplicationFilterRedis : IFilter
    {
        public bool Process(Envelope envelope)
        {
            var outgoingFilter = new OutgoingFilter(new MessageDeduplicationPersistorRedis());

            return outgoingFilter.Process(envelope);
        }

        public IBus Bus { get; set; }
    }
}
