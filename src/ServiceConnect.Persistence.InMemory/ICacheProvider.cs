namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Interface for caching providers
/// </summary>
public interface ICacheProvider
{
    /// <summary>
    /// Occurs after a cache key is removed.
    /// </summary>
    event EventHandler<KeyRemovedEventArgs> KeyRemoved;

    /// <summary>
    /// Add a value to the cache with a relative expiry time, e.g 10 minutes.
    /// </summary>
    void Add<TKey, TValue>(TKey key, TValue value, TimeSpan slidingExpiry, CacheItemPriority priority = CacheItemPriority.Normal);

    /// <summary>
    /// Add a value to the cache with an absolute time, e.g. 01/01/2020.
    /// </summary>
    void Add<TKey, TValue>(TKey key, TValue value, DateTimeOffset absoluteExpiry, CacheItemPriority priority = CacheItemPriority.Normal);

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

    /// <summary>
    /// Replaces the value for an existing key without resetting its expiry timer or
    /// sliding-time window. No-ops if the key is not present.
    /// </summary>
    void Update<TKey, TValue>(TKey key, TValue value);

    /// <summary>
    /// Add a value that never expires. No timer is scheduled and no sliding window is
    /// maintained — the entry persists until <see cref="Remove{TKey}"/>,
    /// <see cref="Clear"/>, or <see cref="PurgeNormalPriorities"/> removes it.
    /// Intended for caller-managed state (e.g. saga/aggregator persistence) where a
    /// background expiry would silently drop in-flight data.
    /// </summary>
    void Add<TKey, TValue>(TKey key, TValue value, CacheItemPriority priority = CacheItemPriority.Normal);
}
