using System;
using System.Threading;
using System.Threading.Tasks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters
{
    public class IncomingDeduplicationFilter : IFilter
    {
        private static readonly Lazy<IncomingFilter> _incomingFilter = new(() =>
            new IncomingFilter(PersistorFactory.Create(DeduplicationFilterSettings.Instance.PersistorType)));

        public IBus Bus { get; set; }

        public Task<bool> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_incomingFilter.Value.Process(envelope));
        }
    }
}
