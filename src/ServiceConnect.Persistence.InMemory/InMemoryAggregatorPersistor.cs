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
    public Task InsertDataAsync(object data, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_memoryCacheLock)
        {
            if (_provider.Contains(name))
            {
                var cacheItem = _provider.Get<string, object>(name);
                ((IList<object>)cacheItem).Add(data);
            }
            else
            {
                _provider.Add(name, new List<object> { data }, _timeProvider.GetUtcNow().Add(ExpiryDuration));
            }
        }
        return Task.CompletedTask;
    }

    public Task<IList<object>> GetDataAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_memoryCacheLock)
        {
            if (_provider.Contains(name))
            {
                // Pre-size the copy to the source list count to avoid resize, and
                // allocate only a single new list (not two) per retrieval (P-36).
                var source = (List<object>)_provider.Get<string, object>(name);
                var copy = new List<object>(source.Count);
                copy.AddRange(source);
                return Task.FromResult<IList<object>>(copy);
            }
            return Task.FromResult<IList<object>>([]);
        }
    }

    public Task RemoveDataAsync(string name, Guid correlationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_memoryCacheLock)
        {
            if (_provider.Contains(name))
            {
                var cacheItem = (List<object>)_provider.Get<string, object>(name);
                for (var index = 0; index < cacheItem.Count; index++)
                {
                    if (cacheItem[index] is Message message && message.CorrelationId == correlationId)
                    {
                        cacheItem.RemoveAt(index);
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
            {
                _provider.Remove(name);
            }
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
                var cacheItem = (List<object>)_provider.Get<string, object>(name);
                return Task.FromResult(cacheItem.Count);
            }
            return Task.FromResult(0);
        }
    }
}
