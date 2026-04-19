namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Defines key-based storage operations used by the in-memory persistence components.
/// </summary>
public interface IKeyValueStore
{
    /// <summary>
    /// Adds a value that expires at the specified absolute time.
    /// </summary>
    void Add<TKey, TValue>(TKey key, TValue value, DateTimeOffset absoluteExpiry, CacheItemPriority priority = CacheItemPriority.Normal);

    /// <summary>
    /// Gets the value stored for the specified key.
    /// </summary>
    TValue Get<TKey, TValue>(TKey key);

    /// <summary>
    /// Removes the value stored for the specified key.
    /// </summary>
    void Remove<TKey>(TKey key);

    /// <summary>
    /// Returns all keys currently stored.
    /// </summary>
    IEnumerable<object> Keys();

    /// <summary>
    /// Determines whether the specified key exists.
    /// </summary>
    bool Contains<TKey>(TKey key);

    /// <summary>
    /// Replaces the value stored for an existing key.
    /// </summary>
    void Update<TKey, TValue>(TKey key, TValue value);
}
