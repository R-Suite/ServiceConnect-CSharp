using System.Collections.Concurrent;

namespace ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer.Filters;

/// <summary>
/// Per-process dedupe persistor. Suitable for the sample only — does not
/// survive process restart and does not coordinate across replicas.
/// For production, see the README's "scaling out" appendix.
/// </summary>
public sealed class InMemoryDedupePersistor : IDedupePersistor
{
    private readonly ConcurrentDictionary<Guid, DateTime> _seen = new();

    public Task<bool> ContainsAsync(Guid messageId, CancellationToken cancellationToken = default)
        => Task.FromResult(_seen.ContainsKey(messageId));

    public Task<bool> TryInsertAsync(Guid messageId, DateTime expiry, CancellationToken cancellationToken = default)
        => Task.FromResult(_seen.TryAdd(messageId, expiry));
}
