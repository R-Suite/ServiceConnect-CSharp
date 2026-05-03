namespace ServiceConnect.Persistence.InMemory;

internal sealed class InMemoryPersistenceState : IDisposable
{
    private int _disposed;
    private readonly IDisposable? _ownedProvider;

    public InMemoryPersistenceState(TimeProvider? timeProvider = null)
    {
        var cp = new CacheProvider(timeProvider);
        Provider = cp;
        _ownedProvider = cp;
    }

    /// <summary>
    /// Test-seam constructor: accepts an externally-supplied <see cref="ICacheProvider"/>
    /// whose <see cref="ICacheProvider.Contains{TKey}"/> and
    /// <see cref="ICacheProvider.TryGet{TKey,TValue}"/> can be controlled independently,
    /// enabling deterministic reproduction of the Contains→TryGet concurrency window.
    /// </summary>
    internal InMemoryPersistenceState(ICacheProvider provider)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _ownedProvider = null; // caller owns lifetime
    }

    public ICacheProvider Provider { get; }
    public ReaderWriterLockSlim SyncRoot { get; } = new();
    public SortedSet<TimeoutEntry> TimeoutIndex { get; } = new(TimeoutEntryComparer.Instance);
    public Dictionary<Guid, TimeoutEntry> TimeoutsById { get; } = [];

    /// <summary>
    /// Disposes the owned <see cref="CacheProvider"/> (which holds <see cref="ITimer"/>
    /// registrations) and the <see cref="ReaderWriterLockSlim"/> (which holds kernel
    /// handles). Without this, every DI rebuild leaks both. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _ownedProvider?.Dispose();
        SyncRoot.Dispose();
    }
}
