using System;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class OutgoingDeduplicationFilterRedis : IFilter
    {
        private static readonly Lazy<OutgoingFilter> _outgoingFilter = new(() =>
            new OutgoingFilter(new MessageDeduplicationPersistorRedis()));

        public bool Process(Envelope envelope)
        {
            return _outgoingFilter.Value.Process(envelope);
        }

        public IBus Bus { get; set; }
    }
}
