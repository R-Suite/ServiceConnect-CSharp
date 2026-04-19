namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Defines the retention priority assigned to cached items.
/// </summary>
public enum CacheItemPriority
{
    /// <summary>
    /// Indicates standard cache retention behavior.
    /// </summary>
    Normal,

    /// <summary>
    /// Indicates the item should be retained ahead of normal-priority items.
    /// </summary>
    High,
}
