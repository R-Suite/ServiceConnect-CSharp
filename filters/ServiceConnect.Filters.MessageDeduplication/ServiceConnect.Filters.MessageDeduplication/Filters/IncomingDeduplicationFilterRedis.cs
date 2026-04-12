using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class IncomingDeduplicationFilterRedis : IFilter
    {
        private static readonly Lazy<IncomingFilter> _incomingFilter = new(() =>
            new IncomingFilter(new MessageDeduplicationPersistorRedis()));

        public bool Process(Envelope envelope)
        {
            return _incomingFilter.Value.Process(envelope);
        }

        public IBus Bus { get; set; }
    }
}
