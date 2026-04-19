using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistence.InMemory;

public sealed class InMemoryAggregatorPersistor : IAggregatorPersistor
{
    private readonly TimeProvider _timeProvider;
    private readonly CacheProvider _provider;

    // Parameters required by IAggregatorPersistor factory convention but unused in InMemory implementation
    public InMemoryAggregatorPersistor(string connectionString, string databaseName, string collectionName, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _provider = new CacheProvider(_timeProvider);
    }
#if NET9_0_OR_GREATER
    private readonly Lock _memoryCacheLock = new();
#else
    private readonly object _memoryCacheLock = new();
#endif

    private static readonly TimeSpan ExpiryDuration = TimeSpan.FromDays(2);

    private sealed record Entry(Guid Id, object Data);

    public Task InsertDataAsync(object data, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_memoryCacheLock)
        {
            var list = GetOrCreateEntries(name);
            list.Add(new Entry(Guid.NewGuid(), data));
        }
        return Task.CompletedTask;
    }

    public Task<IList<object>> GetDataAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_memoryCacheLock)
        {
            if (!_provider.Contains(name))
                return Task.FromResult<IList<object>>([]);

            var source = (List<Entry>)_provider.Get<string, object>(name);
            var copy = new List<object>(source.Count);
            foreach (var entry in source)
                copy.Add(entry.Data);
            return Task.FromResult<IList<object>>(copy);
        }
    }

    public Task<IAggregatorSnapshot> GetSnapshotAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_memoryCacheLock)
        {
            if (!_provider.Contains(name))
                return Task.FromResult<IAggregatorSnapshot>(AggregatorSnapshot.Empty);

            var source = (List<Entry>)_provider.Get<string, object>(name);
            var messages = new List<object>(source.Count);
            var ids = new List<Guid>(source.Count);
            foreach (var entry in source)
            {
                messages.Add(entry.Data);
                ids.Add(entry.Id);
            }
            return Task.FromResult<IAggregatorSnapshot>(new AggregatorSnapshot(messages, ids, 0));
        }
    }

    public Task RemoveDataAsync(string name, Guid correlationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_memoryCacheLock)
        {
            if (_provider.Contains(name))
            {
                var list = (List<Entry>)_provider.Get<string, object>(name);
                for (var index = 0; index < list.Count; index++)
                {
                    if (list[index].Data is Message message && message.CorrelationId == correlationId)
                    {
                        list.RemoveAt(index);
                        break;
                    }
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task RemoveAllAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_memoryCacheLock)
        {
            if (_provider.Contains(name))
                _provider.Remove(name);
        }
        return Task.CompletedTask;
    }

    public Task RemoveSnapshotAsync(string name, IAggregatorSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.ResolvedIds.Count == 0) return Task.CompletedTask;

        lock (_memoryCacheLock)
        {
            if (!_provider.Contains(name)) return Task.CompletedTask;

            var list = (List<Entry>)_provider.Get<string, object>(name);
            var idsToRemove = new HashSet<Guid>(snapshot.ResolvedIds);
            list.RemoveAll(entry => idsToRemove.Contains(entry.Id));

            if (list.Count == 0)
                _provider.Remove(name);
        }
        return Task.CompletedTask;
    }

    public Task<int> CountAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_memoryCacheLock)
        {
            if (_provider.Contains(name))
            {
                var list = (List<Entry>)_provider.Get<string, object>(name);
                return Task.FromResult(list.Count);
            }
            return Task.FromResult(0);
        }
    }

    private List<Entry> GetOrCreateEntries(string name)
    {
        if (_provider.Contains(name))
            return (List<Entry>)_provider.Get<string, object>(name);

        var list = new List<Entry>();
        _provider.Add(name, list, _timeProvider.GetUtcNow().Add(ExpiryDuration));
        return list;
    }
}
