using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Persistence.InMemory;

public sealed class InMemoryTimeoutStore : ITimeoutStore
{
    private readonly TimeProvider _timeProvider;
    private readonly InMemoryPersistenceState _state;

    private static readonly TimeSpan DefaultNextQueryInterval = TimeSpan.FromMinutes(1);

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

        _state.SyncRoot.EnterWriteLock();
        try
        {
            if (_state.TimeoutsById.ContainsKey(timeoutData.Id))
                throw new PersistenceException($"TimeoutData with Id {timeoutData.Id} already exists.");

            var entry = new TimeoutEntry(timeoutData.Time, timeoutData.Id, timeoutData);
            _state.TimeoutsById[timeoutData.Id] = entry;
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
        var nextQueryTime = DateTimeOffset.MaxValue;

        _state.SyncRoot.EnterReadLock();
        try
        {
            foreach (var entry in _state.TimeoutIndex)
            {
                if (entry.Time <= utcNow)
                {
                    retval.DueTimeouts.Add(entry.Data);
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
            _state.SyncRoot.ExitReadLock();
        }

        if (nextQueryTime == DateTimeOffset.MaxValue)
            nextQueryTime = utcNow.Add(DefaultNextQueryInterval);

        retval.NextQueryTime = nextQueryTime;
        return Task.FromResult(retval);
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
