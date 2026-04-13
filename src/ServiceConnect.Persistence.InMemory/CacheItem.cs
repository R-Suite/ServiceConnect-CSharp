namespace ServiceConnect.Persistence.InMemory;

public sealed class CacheItem
{
    public CacheItem() { }

    /// <summary>
    /// Initializes a new <see cref="CacheItem"/> with a value, priority, and optional
    /// relative expiry duration. A null <paramref name="relativeExpiry"/> disables sliding expiry.
    /// </summary>
    public CacheItem(object value, CacheItemPriority priority, TimeSpan? relativeExpiry = null)
    {
        Value = value;
        Priority = priority;
        RelativeExpiry = relativeExpiry;
    }

    /// <summary>Cached value.</summary>
    public object? Value { get; set; }

    /// <summary>Priority controlling whether this item is subject to purge sweeps.</summary>
    public CacheItemPriority Priority { get; set; }

    /// <summary>Sliding expiry window; null for absolute expiry.</summary>
    public TimeSpan? RelativeExpiry { get; set; }
}
