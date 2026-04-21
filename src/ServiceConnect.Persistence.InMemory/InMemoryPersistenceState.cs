namespace ServiceConnect.Persistence.InMemory;

internal sealed class InMemoryPersistenceState : IDisposable
{
    private int _disposed;

    public InMemoryPersistenceState(TimeProvider? timeProvider = null)
    {
        Provider = new CacheProvider(timeProvider);
    }

    public CacheProvider Provider { get; }
    public ReaderWriterLockSlim SyncRoot { get; } = new();
    public SortedSet<TimeoutEntry> TimeoutIndex { get; } = new(TimeoutEntryComparer.Instance);
    public Dictionary<Guid, TimeoutEntry> TimeoutsById { get; } = new();

    /// <summary>
    /// Disposes the owned <see cref="CacheProvider"/> (which holds <see cref="ITimer"/>
    /// registrations) and the <see cref="ReaderWriterLockSlim"/> (which holds kernel
    /// handles). Without this, every DI rebuild leaks both. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Provider.Dispose();
        SyncRoot.Dispose();
    }
}
