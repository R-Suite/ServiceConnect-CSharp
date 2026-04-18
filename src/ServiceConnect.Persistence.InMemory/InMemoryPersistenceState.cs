namespace ServiceConnect.Persistence.InMemory;

internal sealed class InMemoryPersistenceState
{
    public InMemoryPersistenceState(TimeProvider? timeProvider = null)
    {
        Provider = new CacheProvider(timeProvider);
    }

    public CacheProvider Provider { get; }
    public ReaderWriterLockSlim SyncRoot { get; } = new();
    public SortedSet<TimeoutEntry> TimeoutIndex { get; } = new(TimeoutEntryComparer.Instance);
    public Dictionary<Guid, TimeoutEntry> TimeoutsById { get; } = new();
}
