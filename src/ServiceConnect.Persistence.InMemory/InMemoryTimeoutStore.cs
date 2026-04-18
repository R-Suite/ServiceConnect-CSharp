using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Persistence.InMemory;

public sealed class InMemoryTimeoutStore : ITimeoutStore
{
    private readonly TimeProvider _timeProvider;
    private readonly InMemoryPersistenceState _state;

    private static readonly TimeSpan DefaultNextQueryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LockLeaseDuration = TimeSpan.FromMinutes(5);

    public InMemoryTimeoutStore(string connectionString = "", string databaseName = "", TimeProvider? timeProvider = null)
        : this(new InMemoryPersistenceState(timeProvider), timeProvider) { }

    internal InMemoryTimeoutStore(InMemoryPersistenceState state, TimeProvider? timeProvider = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

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
                }
                else
                {
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
}
