namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Interface for caching providers
/// </summary>
internal interface ICacheProvider
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
    /// Tries to get a value from the cache for the specified key.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the key is present (the stored value is written to
    /// <paramref name="value"/>); <see langword="false"/> otherwise. Distinguishes
    /// "key absent" from "key present with null value" — pre-v8's <c>Get</c> returned
    /// <c>default!</c> in both cases.
    /// </returns>
    /// <remarks>
    /// For reference-type <typeparamref name="TValue"/>, <paramref name="value"/> may
    /// be <see langword="null"/> when present (a null was explicitly stored). For
    /// value-type <typeparamref name="TValue"/>, the runtime out-parameter is the
    /// underlying value type — never <see langword="null"/> — and on miss receives
    /// <c>default(TValue)</c> (e.g. <c>0</c> for <see cref="int"/>).
    /// </remarks>
    bool TryGet<TKey, TValue>(TKey key, out TValue? value);

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
    /// sliding-time window.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// Thrown when <paramref name="key"/> is not present, so callers fail deterministically
    /// rather than silently no-op'ing on a missing key. Use
    /// <see cref="Add{TKey,TValue}(TKey, TValue, CacheItemPriority)"/> to insert new keys.
    /// </exception>
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
