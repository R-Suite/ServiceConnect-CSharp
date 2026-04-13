using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistence.InMemory;

public sealed class InMemoryAggregatorPersistor : IAggregatorPersistor
{
    // Parameters required by IAggregatorPersistor factory convention but unused in InMemory implementation
    public InMemoryAggregatorPersistor(string connectionString, string databaseName, string collectionName) { }
#if NET9_0_OR_GREATER
    private readonly Lock _memoryCacheLock = new();
#else
    private readonly object _memoryCacheLock = new();
#endif

    private static readonly TimeSpan ExpiryDuration = TimeSpan.FromDays(2);
    private readonly CacheProvider _provider = new();

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
                _provider.Add(name, new List<object> { data }, DateTime.UtcNow.Add(ExpiryDuration));
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
                var cacheItem = _provider.Get<string, object>(name);
                return Task.FromResult<IList<object>>(((List<object>)cacheItem).ToList());
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
                var message = cacheItem.FirstOrDefault(x => x is Message m && m.CorrelationId == correlationId);
                if (message != null)
                    cacheItem.Remove(message);
            }
        }
        return Task.CompletedTask;
    }

    internal void RemoveAll(string name)
    {
        lock (_memoryCacheLock)
        {
            if (_provider.Contains(name))
            {
                _provider.Remove(name);
            }
        }
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
