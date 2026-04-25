using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Stores timeout messages in process memory for local execution.
/// </summary>
public sealed class InMemoryTimeoutStore : ITimeoutStore
{
    private readonly TimeProvider _timeProvider;
    private readonly InMemoryPersistenceState _state;

    private static readonly TimeSpan LockLeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Initializes a new <see cref="InMemoryTimeoutStore"/> instance.
    /// </summary>
    public InMemoryTimeoutStore(string connectionString = "", string databaseName = "", TimeProvider? timeProvider = null)
        : this(new InMemoryPersistenceState(timeProvider), timeProvider) { }

    internal InMemoryTimeoutStore(InMemoryPersistenceState state, TimeProvider? timeProvider = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Adds a timeout to the in-memory store.
    /// </summary>
    public Task InsertTimeoutAsync(TimeoutData timeoutData, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var storedTimeout = Clone(timeoutData);

        _state.SyncRoot.EnterWriteLock();
        try
        {
            if (_state.TimeoutsById.ContainsKey(storedTimeout.Id))
                throw new PersistenceException($"TimeoutData with Id {storedTimeout.Id} already exists.");

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
    public Task<TimeoutsBatch> GetTimeoutsBatchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var retval = new TimeoutsBatch { DueTimeouts = [] };
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        var sessionId = Guid.NewGuid();

        _state.SyncRoot.EnterWriteLock();
        try
        {
            foreach (var entry in _state.TimeoutIndex)
            {
                if (entry.Time > utcNow)
                    break;

                if (!entry.Data.Locked || entry.Data.LockExpiresAt <= utcNow)
                {
                    entry.Data.Locked = true;
                    entry.Data.LockedBy = sessionId;
                    entry.Data.LockExpiresAt = utcNow + LockLeaseDuration;
                    retval.DueTimeouts.Add(Clone(entry.Data));
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

        return Task.FromResult(retval);
    }

    private static TimeoutData Clone(TimeoutData timeoutData)
    {
        return new TimeoutData
        {
            Id = timeoutData.Id,
            Destination = timeoutData.Destination,
            ProcessManagerId = timeoutData.ProcessManagerId,
            Time = timeoutData.Time,
            Headers = timeoutData.Headers.ToDictionary(static pair => pair.Key, static pair => CloneHeaderValue(pair.Value)),
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
            _ => value,
        };
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
                // Lease-checked: a missing/unleased/mismatched-owner row all mean
                // "this caller no longer holds the lease" — surface as ConcurrencyException
                // so parity with Mongo is preserved and callers don't silently miss
                // invalidations.
                if (!found || !entry!.Data.Locked || entry.Data.LockedBy != owner)
                    throw new ConcurrencyException(
                        $"Lease for timeout '{id}' was invalidated; lock owner '{owner}' no longer holds the lease.");
            }
            else if (!found)
            {
                // Unconditional id-only path: caller's intent is "remove if present".
                return Task.CompletedTask;
            }

            _state.TimeoutsById.Remove(id);
            _state.TimeoutIndex.Remove(entry!);
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
                // Lease-checked: a missing/unleased/mismatched-owner row all mean
                // "this caller no longer holds the lease" — surface as ConcurrencyException
                // so parity with Mongo is preserved and callers don't silently miss
                // invalidations.
                if (!found || !entry!.Data.Locked || entry.Data.LockedBy != owner)
                    throw new ConcurrencyException(
                        $"Lease for timeout '{id}' was invalidated; lock owner '{owner}' no longer holds the lease.");
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
