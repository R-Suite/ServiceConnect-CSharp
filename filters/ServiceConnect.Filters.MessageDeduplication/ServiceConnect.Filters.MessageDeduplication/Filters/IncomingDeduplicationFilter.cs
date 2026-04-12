using System;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class IncomingDeduplicationFilter : IFilter
    {
        private static readonly Lazy<IncomingFilter> _incomingFilter = new(() =>
            new IncomingFilter(PersistorFactory.Create(DeduplicationFilterSettings.Instance.PersistorType)));

        public IBus Bus { get; set; }

        public bool Process(Envelope envelope)
        {
            return _incomingFilter.Value.Process(envelope);
        }
    }
}
