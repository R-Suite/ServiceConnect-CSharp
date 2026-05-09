using System.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Stores timeout messages in process memory for local execution.
/// </summary>
/// <remarks>
/// <b>Intended for development and tests.</b> Scheduled timeouts are held in-process and are
/// lost on restart. Use a durable <see cref="ServiceConnect.Interfaces.ITimeoutStore"/>
/// implementation (e.g. the MongoDB timeout store) for production.
/// </remarks>
public sealed class InMemoryTimeoutStore : ITimeoutStore, IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly InMemoryPersistenceState _state;
    private readonly TimeSpan _lockLeaseDuration;

    // True when this store created its own _state in the public ctor; false when an
    // external state was supplied via the internal ctor (e.g. shared by a sibling
    // InMemoryProcessManagerFinder). Dispose only tears down the state when owned.
    private readonly bool _ownsState;
    private int _disposed;

    /// <summary>
    /// Initializes a new <see cref="InMemoryTimeoutStore"/> instance with the supplied options.
    /// </summary>
    public InMemoryTimeoutStore(InMemoryPersistenceOptions options, TimeProvider? timeProvider = null)
        : this(options, new InMemoryPersistenceState(timeProvider), timeProvider)
    {
        _ownsState = true;
    }

    internal InMemoryTimeoutStore(
        InMemoryPersistenceOptions options,
        InMemoryPersistenceState state,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.LockLeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.LockLeaseDuration,
                $"{nameof(InMemoryPersistenceOptions.LockLeaseDuration)} must be positive.");
        }
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lockLeaseDuration = options.LockLeaseDuration;
        _ownsState = false;
    }

    /// <summary>
    /// Adds a timeout to the in-memory store.
    /// </summary>
    public Task InsertTimeoutAsync(TimeoutData timeoutData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeoutData);
        if (timeoutData.Id == Guid.Empty)
        {
            throw new ArgumentException("TimeoutData.Id must not be Guid.Empty.", nameof(timeoutData));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var storedTimeout = Clone(timeoutData);

        _state.SyncRoot.EnterWriteLock();
        try
        {
            if (_state.TimeoutsById.ContainsKey(storedTimeout.Id))
            {
                throw new PersistenceException($"TimeoutData with Id {storedTimeout.Id} already exists.");
            }

            var entry = new TimeoutEntry(storedTimeout.Time, storedTimeout.Id, storedTimeout);
            _state.TimeoutsById[storedTimeout.Id] = entry;
            _state.TimeoutIndex.Add(entry);
        }
        finally
        {
            _state.SyncRoot.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns due timeouts. Callers poll on a fixed cadence; per-batch next-query hints
    /// are not exposed because no consumer reads them.
    /// </summary>
    public Task<TimeoutsBatch> GetTimeoutsBatchAsync(int? batchSize = null, CancellationToken cancellationToken = default)
    {
        if (batchSize.HasValue && batchSize.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize.Value, "batchSize must be greater than zero when supplied.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        var sessionId = Guid.NewGuid();
        var due = new List<TimeoutData>();

        _state.SyncRoot.EnterWriteLock();
        try
        {
            foreach (var entry in _state.TimeoutIndex)
            {
                if (entry.Time > utcNow)
                {
                    break;
                }

                if (!entry.Data.Locked || entry.Data.LockExpiresAt <= utcNow)
                {
                    // In-place mutation under the write lock: _state.TimeoutsById and _state.TimeoutIndex
                    // hold the same TimeoutEntry reference, so the index is the single source of truth.
                    // The clone in due.Add isolates the caller from subsequent mutations.
                    entry.Data.Locked = true;
                    entry.Data.LockedBy = sessionId;
                    entry.Data.LockExpiresAt = utcNow + _lockLeaseDuration;
                    due.Add(Clone(entry.Data));

                    if (batchSize is { } cap && due.Count >= cap)
                    {
                        break;
                    }
                }
                // Due-but-leased rows are skipped this poll; the next fixed-cadence poll
                // (or the lease reaper if one is configured) will reclaim them once the
                // lease expires.
            }
        }
        finally
        {
            _state.SyncRoot.ExitWriteLock();
        }

        return Task.FromResult(new TimeoutsBatch { DueTimeouts = due });
    }

    private static TimeoutData Clone(TimeoutData timeoutData)
    {
        return new TimeoutData
        {
            Id = timeoutData.Id,
            Destination = timeoutData.Destination,
            ProcessManagerId = timeoutData.ProcessManagerId,
            Time = timeoutData.Time,
            Headers = timeoutData.Headers.ToDictionary(static pair => pair.Key, static pair => CloneHeaderValue(pair.Value), StringComparer.Ordinal),
            Locked = timeoutData.Locked,
            LockedBy = timeoutData.LockedBy,
            LockExpiresAt = timeoutData.LockExpiresAt,
        };
    }

    private static object CloneHeaderValue(object value)
    {
        return value switch
        {
            byte[] bytes => (byte[])bytes.Clone(),
            // Strings and value types are immutable / pass-by-value — return as-is.
            string or ValueType => value,
            // Any other reference type: deep-clone to prevent caller mutations leaking
            // into stored snapshot. Mirror the deep-clone behaviour the aggregator
            // persistor uses on Insert/Get for the same reason.
            _ => DeepClone.Clone(value),
        };
    }

    /// <summary>
    /// Disposes the owned <see cref="InMemoryPersistenceState"/> if this store created
    /// it (public ctor path). Externally-supplied state (internal ctor path) is left
    /// alone — its lifetime belongs to the supplier.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsState)
        {
            _state.Dispose();
        }
    }

    /// <inheritdoc />
    public Task RemoveDispatchedTimeoutAsync(Guid id, Guid? lockOwner = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _state.SyncRoot.EnterWriteLock();
        try
        {
            var found = _state.TimeoutsById.TryGetValue(id, out var entry);

            if (lockOwner is { } owner)
            {
                // Lease-checked: a missing/unleased/mismatched-owner row, or a row whose
                // lease window has elapsed, all mean "this caller no longer holds the lease"
                // — surface as ConcurrencyException so parity with Mongo is preserved and
                // callers don't silently miss invalidations.
                var utcNow = _timeProvider.GetUtcNow();
                if (!found
                    || !entry!.Data.Locked
                    || entry.Data.LockedBy != owner
                    || entry.Data.LockExpiresAt is null
                    || entry.Data.LockExpiresAt <= utcNow)
                {
                    throw new ConcurrencyException(
                        $"Lease for timeout '{id}' was invalidated; lock owner '{owner}' no longer holds the lease.");
                }
            }
            else if (!found)
            {
                // Unconditional id-only path: caller's intent is "remove if present".
                return Task.CompletedTask;
            }

            _state.TimeoutsById.Remove(id);
            var removed = _state.TimeoutIndex.Remove(entry!);
            Debug.Assert(removed, "TimeoutIndex.Remove returned false; comparer drift between insert and remove.");
        }
        finally
        {
            _state.SyncRoot.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReleaseDispatchedTimeoutAsync(Guid id, Guid? lockOwner = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _state.SyncRoot.EnterWriteLock();
        try
        {
            var found = _state.TimeoutsById.TryGetValue(id, out var entry);

            if (lockOwner is { } owner)
            {
                // Lease-checked: a missing/unleased/mismatched-owner row, or a row whose
                // lease window has elapsed, all mean "this caller no longer holds the lease"
                // — surface as ConcurrencyException so parity with Mongo is preserved and
                // callers don't silently miss invalidations.
                var utcNow = _timeProvider.GetUtcNow();
                if (!found
                    || !entry!.Data.Locked
                    || entry.Data.LockedBy != owner
                    || entry.Data.LockExpiresAt is null
                    || entry.Data.LockExpiresAt <= utcNow)
                {
                    throw new ConcurrencyException(
                        $"Lease for timeout '{id}' was invalidated; lock owner '{owner}' no longer holds the lease.");
                }
            }
            else if (!found)
            {
                // Unconditional id-only path: caller's intent is "release if present".
                return Task.CompletedTask;
            }

            entry!.Data.Locked = false;
            entry.Data.LockedBy = Guid.Empty;
            entry.Data.LockExpiresAt = null;
        }
        finally
        {
            _state.SyncRoot.ExitWriteLock();
        }

        return Task.CompletedTask;
    }
}
