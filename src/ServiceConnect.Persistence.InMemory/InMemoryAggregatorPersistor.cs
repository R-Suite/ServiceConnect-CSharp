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

    public void InsertData(object data, string name)
    {
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
    }

    public IList<object> GetData(string name)
    {
        lock (_memoryCacheLock)
        {
            if (_provider.Contains(name))
            {
                var cacheItem = _provider.Get<string, object>(name);
                return ((List<object>)cacheItem).ToList();
            }
            return [];
        }
    }

    public void RemoveData(string name, Guid correlationId)
    {
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
    }

    public void RemoveAll(string name)
    {
        lock (_memoryCacheLock)
        {
            if (_provider.Contains(name))
            {
                _provider.Remove(name);
            }
        }
    }

    public int Count(string name)
    {
        lock (_memoryCacheLock)
        {
            if (_provider.Contains(name))
            {
                var cacheItem = (List<object>)_provider.Get<string, object>(name);
                return cacheItem.Count;
            }
            return 0;
        }
    }
}
