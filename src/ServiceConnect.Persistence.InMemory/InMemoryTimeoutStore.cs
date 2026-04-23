using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Stores timeout messages in process memory for local execution.
/// </summary>
public sealed class InMemoryTimeoutStore : ITimeoutStore, ILeaseAwareTimeoutStore
{
    private readonly TimeProvider _timeProvider;
    private readonly InMemoryPersistenceState _state;

    private static readonly TimeSpan DefaultNextQueryInterval = TimeSpan.FromMinutes(1);
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
    /// Returns due timeouts and the next time the store should be queried.
    /// </summary>
    public Task<TimeoutsBatch> GetTimeoutsBatchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var retval = new TimeoutsBatch { DueTimeouts = [] };
        DateTimeOffset utcNow = _timeProvider.GetUtcNow();
        var sessionId = Guid.NewGuid();
        var nextQueryTime = DateTimeOffset.MaxValue;

        _state.SyncRoot.EnterWriteLock();
        try
        {
            foreach (var entry in _state.TimeoutIndex)
            {
                if (entry.Time <= utcNow)
                {
                    if (!entry.Data.Locked || entry.Data.LockExpiresAt <= utcNow)
                    {
                        entry.Data.Locked = true;
                        entry.Data.LockedBy = sessionId;
                        entry.Data.LockExpiresAt = utcNow + LockLeaseDuration;
                        retval.DueTimeouts.Add(Clone(entry.Data));
                    }
                    else if (entry.Data.LockExpiresAt is { } leaseExpiry && leaseExpiry < nextQueryTime)
                    {
                        // Due row leased by another worker. Without this branch
                        // we would skip it silently and rely solely on the first
                        // future entry for NextQueryTime — which could push the
                        // re-poll well past this lease's expiry, leaving the row
                        // un-dispatched after the worker that held it crashed.
                        nextQueryTime = leaseExpiry;
                    }
                }
                else
                {
                    if (entry.Time < nextQueryTime)
                        nextQueryTime = entry.Time;
                    break;
                }
            }
        }
        finally
        {
            _state.SyncRoot.ExitWriteLock();
        }

        if (nextQueryTime == DateTimeOffset.MaxValue)
            nextQueryTime = utcNow.Add(DefaultNextQueryInterval);

        retval.NextQueryTime = nextQueryTime;
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

    /// <summary>
    /// Permanently removes a timeout after it has been dispatched.
    /// </summary>
    public Task RemoveDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _state.SyncRoot.EnterWriteLock();
        try
        {
            if (_state.TimeoutsById.TryGetValue(id, out var entry))
            {
                _state.TimeoutsById.Remove(id);
                _state.TimeoutIndex.Remove(entry);
            }
        }
        finally
        {
            _state.SyncRoot.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Releases a previously locked timeout so it can be dispatched again.
    /// </summary>
    public Task ReleaseDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _state.SyncRoot.EnterWriteLock();
        try
        {
            if (_state.TimeoutsById.TryGetValue(id, out var entry))
            {
                entry.Data.Locked = false;
                entry.Data.LockedBy = Guid.Empty;
                entry.Data.LockExpiresAt = null;
            }
        }
        finally
        {
            _state.SyncRoot.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveDispatchedTimeoutAsync(Guid id, Guid lockOwner, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _state.SyncRoot.EnterWriteLock();
        try
        {
            // A missing row or mismatched owner both mean "this caller no longer holds
            // the lease for this timeout" — surface as ConcurrencyException so parity
            // with Mongo is preserved and callers don't silently miss invalidations.
            if (!_state.TimeoutsById.TryGetValue(id, out var entry) || entry.Data.LockedBy != lockOwner)
            {
                throw new ConcurrencyException(
                    $"Lease for timeout '{id}' was invalidated; lock owner '{lockOwner}' no longer holds the lease.");
            }

            _state.TimeoutsById.Remove(id);
            _state.TimeoutIndex.Remove(entry);
        }
        finally
        {
            _state.SyncRoot.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReleaseDispatchedTimeoutAsync(Guid id, Guid lockOwner, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _state.SyncRoot.EnterWriteLock();
        try
        {
            if (!_state.TimeoutsById.TryGetValue(id, out var entry) || entry.Data.LockedBy != lockOwner)
            {
                throw new ConcurrencyException(
                    $"Lease for timeout '{id}' was invalidated; lock owner '{lockOwner}' no longer holds the lease.");
            }

            entry.Data.Locked = false;
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
