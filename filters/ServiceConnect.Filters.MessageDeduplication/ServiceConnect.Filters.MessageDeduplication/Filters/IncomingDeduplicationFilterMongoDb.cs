using System;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class IncomingDeduplicationFilterMongoDb : IFilter
    {
        private static readonly Lazy<IncomingFilter> _incomingFilter = new(() =>
            new IncomingFilter(new MessageDeduplicationPersistorMongoDb()));

        public bool Process(Envelope envelope)
        {
            return _incomingFilter.Value.Process(envelope);
        }

        public IBus Bus { get; set; }
    }
}
