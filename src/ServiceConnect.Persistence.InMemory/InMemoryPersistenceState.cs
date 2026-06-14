namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Shared in-process state store for the in-memory persistence implementation.
/// </summary>
/// <remarks>
/// <b>Intended for development and tests.</b> All state held by this type is in-process and
/// is not durable across restarts. Use <c>UseMongoDbPersistence</c> or another durable
/// persistor for production. <c>UseInMemoryPersistence</c> emits a startup warning when this
/// type is materialised; see that method's remarks for filtering guidance.
/// </remarks>
internal sealed class InMemoryPersistenceState : IDisposable
{
    private int _disposed;
    private readonly IDisposable? _ownedProvider;
    private readonly IDisposable? _ownedSagaProvider;

    public InMemoryPersistenceState(TimeProvider? timeProvider = null)
    {
        var cp = new CacheProvider(timeProvider);
        Provider = cp;
        _ownedProvider = cp;

        // Dedicated saga store. Public IKeyValueStore consumers see Provider only;
        // saga state lives in this private SagaProvider where no user code can reach it.
        var sagaCp = new CacheProvider(timeProvider);
        SagaProvider = sagaCp;
        _ownedSagaProvider = sagaCp;
    }

    /// <summary>
    /// Test-seam constructor: accepts externally-supplied <see cref="ICacheProvider"/>s
    /// so tests can control both behaviours independently — e.g. to deterministically
    /// reproduce the Contains→TryGet concurrency window in the saga finder.
    /// </summary>
    internal InMemoryPersistenceState(ICacheProvider provider, ICacheProvider sagaProvider)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        SagaProvider = sagaProvider ?? throw new ArgumentNullException(nameof(sagaProvider));
        _ownedProvider = null;    // caller owns lifetime
        _ownedSagaProvider = null;
    }

    public ICacheProvider Provider { get; }

    /// <summary>
    /// Saga-specific store used exclusively by <see cref="InMemoryProcessManagerFinder"/>.
    /// Not registered in DI and not reachable through the public <see cref="IKeyValueStore"/>
    /// or <see cref="ICacheProvider"/> surface, so user code cannot observe or corrupt saga state.
    /// </summary>
    public ICacheProvider SagaProvider { get; }

    public ReaderWriterLockSlim SyncRoot { get; } = new();
    public SortedSet<TimeoutEntry> TimeoutIndex { get; } = new(TimeoutEntryComparer.Instance);
    public Dictionary<Guid, TimeoutEntry> TimeoutsById { get; } = [];

    /// <summary>
    /// Disposes the owned <see cref="CacheProvider"/> instances (which hold <see cref="ITimer"/>
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
        _ownedSagaProvider?.Dispose();
        SyncRoot.Dispose();
    }
}
