namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Initializes a new <see cref="CacheItem"/> with a value, priority, and optional
/// relative expiry duration. A null <paramref name="relativeExpiry"/> disables sliding expiry.
/// </summary>
internal sealed class CacheItem(object value, CacheItemPriority priority, TimeSpan? relativeExpiry = null)
{

    /// <summary>Cached value.</summary>
    public object? Value { get; init; } = value;

    /// <summary>Priority controlling whether this item is subject to purge sweeps.</summary>
    public CacheItemPriority Priority { get; init; } = priority;

    /// <summary>Sliding expiry window; null for absolute expiry.</summary>
    public TimeSpan? RelativeExpiry { get; init; } = relativeExpiry;
}
