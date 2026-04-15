using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Persistence.InMemory;

public sealed class InMemoryTimeoutStore : ITimeoutStore
{
    private readonly TimeProvider _timeProvider;
    private readonly InMemoryPersistenceState _state;

    private static readonly TimeSpan ExpiryDuration = TimeSpan.FromDays(2);
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
            string key = timeoutData.Id.ToString();

            if (_state.Provider.Contains(key))
                throw new PersistenceException($"TimeoutData with Id {key} already exists in the cache.");

            _state.Provider.Add(key, timeoutData, _timeProvider.GetUtcNow().Add(ExpiryDuration));
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
            foreach (var key in _state.Provider.Keys())
            {
                var value = _state.Provider.Get<string, object>(key.ToString()!);
                if (value is TimeoutData timeoutData)
                {
                    if (timeoutData.Time <= utcNow)
                        retval.DueTimeouts.Add(timeoutData);
                    else if (timeoutData.Time < nextQueryTime)
                        nextQueryTime = timeoutData.Time;
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
            _state.Provider.Remove(id.ToString());
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
        return Task.CompletedTask;
    }
}
