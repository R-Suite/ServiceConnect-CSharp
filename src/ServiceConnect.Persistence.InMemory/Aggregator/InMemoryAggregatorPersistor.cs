using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Stores aggregator messages and snapshots in the process memory of the current application.
/// </summary>
public sealed class InMemoryAggregatorPersistor : IAggregatorPersistor, IDisposable
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

    // Data is nullable because InMemoryAggregatorPersistorUnresolvedCountTests reflects in
    // a null-Data Entry to exercise the GetSnapshotAsync unresolved-count branch. The public
    // Insert path always supplies a non-null IHasCorrelationId.
    private sealed record Entry(Guid Id, IHasCorrelationId? Data);

    /// <summary>
    /// Adds an aggregator message to the named in-memory stream.
    /// </summary>
    public Task InsertDataAsync(IHasCorrelationId data, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(data);
        // Deep-clone before storing so later caller mutations do not bleed into the
        // buffer. Retrieval does the same on the outbound side.
        var stored = DeepClone.Clone(data);
        lock (_memoryCacheLock)
        {
            var list = GetOrCreateEntries(name);
            list.Add(new Entry(Guid.NewGuid(), stored));
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
                // is-pattern narrows to a non-null local — DeepClone.Clone's `where T : notnull`
                // constraint is satisfied. Skip null-Data entries (only producible by the
                // reflection-based unresolved-count test); the public surface never inserts null.
                if (entry.Data is { } data)
                {
                    copy.Add(DeepClone.Clone(data));
                }
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

        // Capture entry references under the lock. The spread into an array is an O(n)
        // pointer copy that lets concurrent InsertDataAsync / RemoveDataAsync proceed
        // while DeepClone (a JSON round-trip per entry) runs outside the lock below.
        // Entry is a sealed record (immutable); the array holds shared references to
        // the same Entry instances that were in the list at lock-release time.
        Entry[] entriesCopy;
        lock (_memoryCacheLock)
        {
            if (!_provider.TryGet<string, object>(name, out var sourceObj) || sourceObj is not List<Entry> source)
            {
                return Task.FromResult<IAggregatorSnapshot>(AggregatorSnapshot.Empty);
            }
            entriesCopy = [.. source];  // shallow copy of reference array; O(n) pointer copy
        }

        var messages = new List<IHasCorrelationId>(entriesCopy.Length);
        var ids = new List<Guid>(entriesCopy.Length);
        var unresolved = 0;
        foreach (var entry in entriesCopy)
        {
            // is-pattern narrows to non-null for DeepClone's `where T : notnull` constraint.
            if (entry.Data is { } data)
            {
                messages.Add(DeepClone.Clone(data));
                ids.Add(entry.Id);
            }
            else
            {
                unresolved++;
            }
        }
        return Task.FromResult<IAggregatorSnapshot>(new AggregatorSnapshot(messages, ids, unresolved));
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
                    // Null-conditional handles the test-only reflection-injected null Data; production
                    // inserts never produce null.
                    if (list[index].Data?.CorrelationId == correlationId)
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
