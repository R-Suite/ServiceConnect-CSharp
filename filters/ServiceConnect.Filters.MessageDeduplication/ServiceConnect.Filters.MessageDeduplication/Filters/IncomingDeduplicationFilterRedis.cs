using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class IncomingDeduplicationFilterRedis : IFilter
    {
        public bool Process(Envelope envelope)
        {
            var incomingFilter = new IncomingFilter(new MessageDeduplicationPersistorRedis());
            
            return incomingFilter.Process(envelope);
        }

        public IBus Bus { get; set; }
    }
}
