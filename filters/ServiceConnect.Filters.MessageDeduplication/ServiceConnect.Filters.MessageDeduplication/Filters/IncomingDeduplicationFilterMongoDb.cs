using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class IncomingDeduplicationFilterMongoDb : IFilter
    {
        private static IncomingFilter _incomingFilter;

        public bool Process(Envelope envelope)
        {
            if (null == _incomingFilter)
            {
                _incomingFilter = new IncomingFilter(new MessageDeduplicationPersistorMongoDb());
            }
            
            return _incomingFilter.Process(envelope);
        }

        public IBus Bus { get; set; }
    }
}
