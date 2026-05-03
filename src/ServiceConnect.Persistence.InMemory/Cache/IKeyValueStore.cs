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
    /// Tries to get a value from the store for the specified key.
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
