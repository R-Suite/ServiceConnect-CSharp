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

    // Cache one accessor per data type so the reflection cost is paid once. Mongo's aggregator
    // persistor matches by serialized DataBson.CorrelationId — accepting any POCO with a
    // Guid CorrelationId property — so InMemory matches its sibling rather than restricting
    // callers to Message-derived types.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<object, Guid?>> CorrelationIdAccessors = new();

    private static Guid? GetCorrelationId(object data)
    {
        if (data is Message message)
        {
            return message.CorrelationId;
        }

        var accessor = CorrelationIdAccessors.GetOrAdd(data.GetType(), static type =>
        {
            var prop = type.GetProperty("CorrelationId", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (prop is null || prop.PropertyType != typeof(Guid) || prop.GetMethod is null)
            {
                // Cache a throw-delegate rather than a null-returning one. A null result
                // would silently prevent RemoveDataAsync from matching any entry, masking
                // the mis-typed data with a misleading ConcurrencyException. Throwing on
                // every invocation gives callers an actionable diagnosis, and the cache
                // stays bounded (one entry per offending type) while a process restart
                // resets it automatically once the type is corrected.
                return obj => throw new InvalidOperationException(
                    $"Aggregator data type '{obj.GetType().FullName}' does not have a public " +
                    "'Guid CorrelationId' property. Add the property or use a Message subtype.");
            }

            return obj => (Guid?)prop.GetValue(obj);
        });
        return accessor(data);
    }

    // Parameters required by IAggregatorPersistor factory convention but unused in InMemory implementation
    /// <summary>
    /// Initializes a new <see cref="InMemoryAggregatorPersistor"/> instance.
    /// </summary>
    public InMemoryAggregatorPersistor(string connectionString, string databaseName, string collectionName, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _provider = new CacheProvider(_timeProvider);
    }
#if NET9_0_OR_GREATER
    private readonly Lock _memoryCacheLock = new();
#else
    private readonly object _memoryCacheLock = new();
#endif

    private sealed record Entry(Guid Id, object Data);

    /// <summary>
    /// Adds an aggregator message to the named in-memory stream.
    /// </summary>
    public Task InsertDataAsync(object data, string name, CancellationToken cancellationToken = default)
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
    public Task<IList<object>> GetDataAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_memoryCacheLock)
        {
            if (!_provider.TryGet<string, object>(name, out var sourceObj) || sourceObj is not List<Entry> source)
            {
                return Task.FromResult<IList<object>>([]);
            }
            var copy = new List<object>(source.Count);
            foreach (var entry in source)
            {
                copy.Add(DeepClone.Clone(entry.Data));
            }

            return Task.FromResult<IList<object>>(copy);
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

        var messages = new List<object>(entriesCopy.Length);
        var ids = new List<Guid>(entriesCopy.Length);
        var unresolved = 0;
        foreach (var entry in entriesCopy)
        {
            if (entry.Data is null)
            {
                unresolved++;
                continue;
            }
            messages.Add(DeepClone.Clone(entry.Data));
            ids.Add(entry.Id);
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
                    if (GetCorrelationId(list[index].Data) is { } id && id == correlationId)
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
