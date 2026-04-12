using System;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class OutgoingDeduplicationFilter : IFilter
    {
        private static readonly Lazy<OutgoingFilter> _outgoingFilter = new(() =>
            new OutgoingFilter(PersistorFactory.Create(DeduplicationFilterSettings.Instance.PersistorType)));

        public IBus Bus { get; set; }

        public bool Process(Envelope envelope)
        {
            return _outgoingFilter.Value.Process(envelope);
        }
    }
}
