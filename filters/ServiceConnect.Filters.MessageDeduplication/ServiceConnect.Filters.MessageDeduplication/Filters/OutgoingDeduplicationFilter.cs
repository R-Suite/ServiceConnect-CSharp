using System;
using System.Threading;
using System.Threading.Tasks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class OutgoingDeduplicationFilter : IFilter
    {
        private static readonly Lazy<OutgoingFilter> _outgoingFilter = new(() =>
            new OutgoingFilter(PersistorFactory.Create(DeduplicationFilterSettings.Instance.PersistorType)));

        public IBus Bus { get; set; }

        public Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_outgoingFilter.Value.Process(envelope));
        }
    }
}
