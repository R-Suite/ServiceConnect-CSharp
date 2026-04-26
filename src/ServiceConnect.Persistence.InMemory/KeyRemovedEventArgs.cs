namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Provides data for the <see cref="CacheProvider.KeyRemoved"/> event,
/// carrying the key whose entry was removed.
/// </summary>
public sealed class KeyRemovedEventArgs(object key) : EventArgs
{
    /// <summary>Gets the key whose entry was removed from the cache.</summary>
    public object Key { get; } = key;
}
