namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Interface for caching providers
/// </summary>
public interface ICacheProvider
{
    event EventHandler KeyRemoved;

    /// <summary>
    /// Add a value to the cache with a relative expiry time, e.g 10 minutes.
    /// </summary>
    void Add<TKey, TValue>(TKey key, TValue value, TimeSpan slidingExpiry, CacheItemPriority priority = CacheItemPriority.Normal);

    /// <summary>
    /// Add a value to the cache with an absolute time, e.g. 01/01/2020.
    /// </summary>
    void Add<TKey, TValue>(TKey key, TValue value, DateTime absoluteExpiry, CacheItemPriority priority = CacheItemPriority.Normal);

    /// <summary>
    /// Gets a value from the cache for specified key.
    /// </summary>
    TValue Get<TKey, TValue>(TKey key);

    /// <summary>
    /// Remove a value from the cache for specified key.
    /// </summary>
    void Remove<TKey>(TKey key);

    /// <summary>
    /// Clears the contents of the cache.
    /// </summary>
    void Clear();

    /// <summary>
    /// Gets an enumerator for keys of a specific type.
    /// </summary>
    IEnumerable<TKey> Keys<TKey>();

    /// <summary>
    /// Gets an enumerator for all the keys.
    /// </summary>
    IEnumerable<object> Keys();

    /// <summary>
    /// Gets the total count of items in cache.
    /// </summary>
    int Count();

    /// <summary>
    /// Purges all cache items with normal priorities.
    /// </summary>
    int PurgeNormalPriorities();

    /// <summary>
    /// Determines whether the cache contains the specified key.
    /// </summary>
    bool Contains<TKey>(TKey key);
}
