using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Stores aggregator messages and snapshots in the process memory of the current application.
/// </summary>
/// <remarks>
/// <b>Intended for development and tests.</b> Aggregator data is held in-process and is not
/// durable across restarts. Use a durable <see cref="ServiceConnect.Interfaces.IAggregatorPersistor"/>
/// implementation (e.g. the MongoDB persistor) for production.
/// </remarks>
internal sealed class InMemoryAggregatorPersistor : IAggregatorPersistor, IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly CacheProvider _provider;
    private int _disposed;

    /// <summary>
    /// Initializes a new <see cref="InMemoryAggregatorPersistor"/> instance.
    /// </summary>
    /// <param name="timeProvider">Time source used by the underlying cache provider; defaults to <see cref="TimeProvider.System"/>.</param>
    public InMemoryAggregatorPersistor(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _provider = new CacheProvider(_timeProvider);
    }
#if NET9_0_OR_GREATER
    private readonly Lock _memoryCacheLock = new();
#else
    private readonly object _memoryCacheLock = new();
#endif

    private sealed record Entry(Guid Id, IHasCorrelationId Data, string IdempotencyKey);

    /// <summary>
    /// Adds an aggregator message to the named in-memory stream, idempotent on
    /// <paramref name="idempotencyKey"/> while the message's row is still buffered.
    /// </summary>
    public Task InsertDataAsync(IHasCorrelationId data, string name, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        // Deep-clone before storing so later caller mutations do not bleed into the
        // buffer. Retrieval does the same on the outbound side.
        var stored = DeepClone.Clone(data);
        lock (_memoryCacheLock)
        {
            var list = GetOrCreateEntries(name);
            // Skip the insert if a buffered row already carries this idempotency key.
            // The check is O(N) over the per-aggregator buffer; aggregators rarely
            // exceed a few hundred rows in normal usage so a HashSet would not pay
            // back its allocation. Once RemoveSnapshotAsync drains a row the key
            // disappears with it; idempotency only protects the active window, which
            // covers the retry-queue redelivery race InsertDataAsync exists to defend.
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
                {
                    return Task.CompletedTask;
                }
            }
            list.Add(new Entry(Guid.NewGuid(), stored, idempotencyKey));
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the stored messages for the named stream.
    /// </summary>
    public Task<IReadOnlyList<IHasCorrelationId>> GetDataAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_memoryCacheLock)
        {
            if (!_provider.TryGet<string, object>(name, out var sourceObj) || sourceObj is not List<Entry> source)
            {
                return Task.FromResult<IReadOnlyList<IHasCorrelationId>>([]);
            }
            var copy = new List<IHasCorrelationId>(source.Count);
            foreach (var entry in source)
            {
                copy.Add(DeepClone.Clone(entry.Data));
            }

            return Task.FromResult<IReadOnlyList<IHasCorrelationId>>(copy);
        }
    }

    /// <summary>
    /// Returns a snapshot of the stored messages for the named stream.
    /// </summary>
    public Task<IAggregatorSnapshot> GetSnapshotAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Hold the lock through DeepClone to guarantee the source Entry.Data is not
        // mutated mid-clone. A previous version released the lock before cloning,
        // relying on the invariant "no aggregator-update path mutates Entry.Data in
        // place" — fragile against future changes; risks serialising a torn object.
        // The clone cost under the lock is acceptable: snapshots are infrequent
        // relative to inserts, and the buffer's purpose is single-pass dispatch.
        //
        // The InMemory persistor cannot produce an unresolved entry: InsertDataAsync
        // rejects null, every entry carries a typed IHasCorrelationId, and there is no
        // deserialise step that could fail. UnresolvedCount is therefore always 0 here.
        // The Mongo persistor reaches the unresolved branch when a stored document's
        // CLR type is no longer registered or doesn't implement the interface; that
        // branch is exercised by MongoDbAggregatorPersistor's tests.
        //
        // No per-snapshot lease is required here: this persistor is per-process and
        // the AggregatorProcessor serialises flushes per aggregator name via its own
        // flushLock — the multi-worker dispatch hazard the Mongo lease defends against
        // doesn't exist in-process. A lease would also break the documented
        // "handler exception → broker redelivers → re-flush" contract because in-memory
        // leases have no TTL to release stranded claims after a handler throw.
        lock (_memoryCacheLock)
        {
            if (!_provider.TryGet<string, object>(name, out var sourceObj) || sourceObj is not List<Entry> source)
            {
                return Task.FromResult<IAggregatorSnapshot>(AggregatorSnapshot.Empty);
            }

            var messages = new List<IHasCorrelationId>(source.Count);
            var ids = new List<Guid>(source.Count);
            foreach (var entry in source)
            {
                messages.Add(DeepClone.Clone(entry.Data));
                ids.Add(entry.Id);
            }
            return Task.FromResult<IAggregatorSnapshot>(new AggregatorSnapshot(messages, ids, UnresolvedCount: 0));
        }
    }

    /// <summary>
    /// Removes the first stored message whose correlation identifier matches the specified value.
    /// </summary>
    public Task RemoveDataAsync(string name, Guid correlationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool removed = false;
        lock (_memoryCacheLock)
        {
            if (_provider.TryGet<string, object>(name, out var listObj) && listObj is List<Entry> list)
            {
                for (var index = 0; index < list.Count; index++)
                {
                    if (list[index].Data.CorrelationId == correlationId)
                    {
                        list.RemoveAt(index);
                        removed = true;
                        break;
                    }
                }
            }
        }
        // Mirror MongoDbAggregatorPersistor's no-op-delete contract so callers across persistors
        // can distinguish a concurrent-removal race from a mismatched key. InMemoryProcessManagerFinder
        // already raises ConcurrencyException on DeleteDataAsync no-ops; keeping aggregator behaviour
        // aligned prevents a silent divergence between persistor families.
        if (!removed)
        {
            throw new ConcurrencyException(
                $"Aggregator row not found: Name='{name}', CorrelationId='{correlationId}'. Row was concurrently removed or caller passed a mismatched key.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes all stored messages for the named stream.
    /// </summary>
    public Task RemoveAllAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_memoryCacheLock)
        {
            if (_provider.Contains(name))
            {
                _provider.Remove(name);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes all entries represented by the supplied snapshot.
    /// </summary>
    public Task RemoveSnapshotAsync(string name, IAggregatorSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.ResolvedIds.Count == 0)
        {
            return Task.CompletedTask;
        }

        lock (_memoryCacheLock)
        {
            if (!_provider.TryGet<string, object>(name, out var listObj) || listObj is not List<Entry> list)
            {
                return Task.CompletedTask;
            }

            var idsToRemove = new HashSet<Guid>(snapshot.ResolvedIds);
            list.RemoveAll(entry => idsToRemove.Contains(entry.Id));

            if (list.Count == 0)
            {
                _provider.Remove(name);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the number of stored messages for the named stream.
    /// </summary>
    public Task<int> CountAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_memoryCacheLock)
        {
            if (_provider.TryGet<string, object>(name, out var listObj) && listObj is List<Entry> list)
            {
                return Task.FromResult(list.Count);
            }
            return Task.FromResult(0);
        }
    }

    /// <summary>
    /// Returns the number of stored messages for the named stream whose CLR type is
    /// currently resolvable.
    /// </summary>
    /// <remarks>
    /// The InMemory persistor cannot produce unresolved entries: <see cref="InsertDataAsync"/>
    /// rejects null and stores typed <see cref="IHasCorrelationId"/> instances directly, so
    /// every record is resolved by definition. This is therefore a thin pass-through to
    /// <see cref="CountAsync"/>. The Mongo persistor, which deserialises lazily on read,
    /// uses a typed <c>$in</c> filter and is the regression backstop for unresolved-aware
    /// gating.
    /// </remarks>
    public Task<int> CountResolvedAsync(string name, CancellationToken cancellationToken = default)
        => CountAsync(name, cancellationToken);

    /// <summary>
    /// Disposes the underlying <see cref="CacheProvider"/>, releasing any timers
    /// it owns. Without this, every DI rebuild leaks timer registrations.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _provider.Dispose();
    }

    private List<Entry> GetOrCreateEntries(string name)
    {
        if (_provider.TryGet<string, object>(name, out var existing) && existing is List<Entry> cached)
        {
            return cached;
        }

        var list = new List<Entry>();
        // Aggregator buffers have no TTL: flush is caller-driven via RemoveSnapshot /
        // RemoveAll. Background expiry must never silently drop buffered messages
        // mid-aggregation.
        _provider.Add(name, list);
        return list;
    }
}
