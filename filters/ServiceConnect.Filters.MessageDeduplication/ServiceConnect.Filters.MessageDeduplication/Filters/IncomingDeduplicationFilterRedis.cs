using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class IncomingDeduplicationFilterRedis : IFilter
    {
        private static IncomingFilter _incomingFilter;

        public bool Process(Envelope envelope)
        {
            if (null == _incomingFilter)
            {
                _incomingFilter = new IncomingFilter(new MessageDeduplicationPersistorRedis());
            }
           
            return _incomingFilter.Process(envelope);
        }

        public IBus Bus { get; set; }
    }
}
