namespace ServiceConnect.Persistence.InMemory;

public interface IKeyValueStore
{
    void Add<TKey, TValue>(TKey key, TValue value, DateTimeOffset absoluteExpiry, CacheItemPriority priority = CacheItemPriority.Normal);
    TValue Get<TKey, TValue>(TKey key);
    void Remove<TKey>(TKey key);
    IEnumerable<object> Keys();
    bool Contains<TKey>(TKey key);
    void Update<TKey, TValue>(TKey key, TValue value);
}
