using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters;

public class IncomingDeduplicationFilterInMemory : IFilter
{
    private static readonly Lazy<IncomingFilter> _incomingFilter = new(() =>
        new IncomingFilter(new MessageDeduplicationPersistorInMemory()));

    public IBus Bus { get; set; } = null!;

    public bool Process(Envelope envelope)
    {
        return _incomingFilter.Value.Process(envelope);
    }
}
