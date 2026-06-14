using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.Filters.Sender;

public sealed class TraceHeaderFilter : IFilter
{
    public Task<FilterAction> ProcessAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        envelope.Headers["X-Trace-Id"] = "trace-001";
        return Task.FromResult(FilterAction.Continue);
    }
}
