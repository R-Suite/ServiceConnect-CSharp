using System;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Filters.MessageDeduplication.Filters;

public class OutgoingDeduplicationFilterInMemory : IFilter
{
    private static readonly Lazy<OutgoingFilter> _outgoingFilter = new(() =>
        new OutgoingFilter(new MessageDeduplicationPersistorInMemory()));

    public IBus Bus { get; set; } = null!;

    public bool Process(Envelope envelope)
    {
        return _outgoingFilter.Value.Process(envelope);
    }
}
