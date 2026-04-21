using System.Collections.Concurrent;

namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Provides an in-memory cache with absolute and sliding expiration support.
/// </summary>
public sealed class CacheProvider : ICacheProvider, IKeyValueStore, IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<object, CacheItem> _cache = new();
    private readonly ConcurrentDictionary<object, SlidingDetails> _slidingTime = new();
    private readonly ConcurrentDictionary<object, ITimer> _timers = new();
    // Serializes compound Add operations so the value swap, sliding window reset, and
    // timer replacement are observed together. Without it, a re-Add after the original
    // TryAdd retained the stale value but StartObserving installed a fresh timer —
    // effectively extending the stale value's TTL.
    private readonly object _addLock = new();
    private int _disposed;

    /// <summary>
    /// Initializes a new <see cref="CacheProvider"/> using the supplied clock.
    /// </summary>
    public CacheProvider(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    #region Implementation of ICacheProvider

    /// <summary>
    /// Occurs after an entry is removed from the cache.
    /// </summary>
    public event EventHandler? KeyRemoved;

    /// <summary>
    /// Add a value to the cache with a relative expiry time, e.g 10 minutes.
    /// </summary>
    public void Add<TKey, TValue>(TKey key, TValue value, TimeSpan slidingExpiry, CacheItemPriority priority = CacheItemPriority.Normal)
    {
        Add(key, value, slidingExpiry, priority, true);
    }

    /// <summary>
    /// Add a value to the cache with an absolute time, e.g. 01/01/2020.
    /// </summary>
    public void Add<TKey, TValue>(TKey key, TValue value, DateTimeOffset absoluteExpiry, CacheItemPriority priority = CacheItemPriority.Normal)
    {
        if (absoluteExpiry < _timeProvider.GetUtcNow())
            throw new ArgumentOutOfRangeException(nameof(absoluteExpiry), "Absolute expiry must be in the future.");

        var diff = absoluteExpiry - _timeProvider.GetUtcNow();
        Add(key, value, diff, priority, false);
    }

    /// <summary>
    /// Add a value that never expires. No timer is scheduled and no sliding window is
    /// maintained — the entry persists until <see cref="Remove{TKey}"/>,
    /// <see cref="Clear"/>, or <see cref="PurgeNormalPriorities"/> removes it.
    /// Intended for caller-managed state (e.g. saga/aggregator persistence) where a
    /// background expiry would silently drop in-flight data.
    /// </summary>
    public void Add<TKey, TValue>(TKey key, TValue value, CacheItemPriority priority = CacheItemPriority.Normal)
    {
        // Matches the timed Add overloads: a re-Add replaces the value and clears any
        // sliding/timer state a prior timed Add left in place.
        lock (_addLock)
        {
            _cache[key!] = new CacheItem(value!, priority, null);
            _slidingTime.TryRemove(key!, out _);
            DisposeTimer(key!);
        }
    }

    /// <summary>
    /// Gets a value from the cache for specified key.
    /// </summary>
    public TValue Get<TKey, TValue>(TKey key)
    {
        if (!_cache.TryGetValue(key!, out var cacheItem))
            return default!;

        if (cacheItem.RelativeExpiry.HasValue && _slidingTime.TryGetValue(key!, out var sliding))
            sliding.Slide();

        return (TValue)cacheItem.Value!;
    }

    /// <summary>
    /// Remove a value from the cache for specified key.
    /// </summary>
    public void Remove<TKey>(TKey key)
    {
        if (!Equals(key, null))
        {
            _cache.TryRemove(key!, out _);
            _slidingTime.TryRemove(key!, out _);
            DisposeTimer(key!);

            KeyRemoved?.Invoke(key, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Clears the contents of the cache.
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
        _slidingTime.Clear();

        foreach (var kvp in _timers)
        {
            kvp.Value.Dispose();
        }
        _timers.Clear();
    }

    /// <summary>
    /// Gets an enumerator for keys of a specific type. Streams the ConcurrentDictionary
    /// snapshot so callers that bail early avoid the full filtered-materialization cost.
    /// </summary>
    public IEnumerable<TKey> Keys<TKey>()
    {
        var typeOfKey = typeof(TKey);
        foreach (var k in _cache.Keys)
        {
            if (k.GetType() == typeOfKey) yield return (TKey)k;
        }
    }

    /// <summary>
    /// Gets an enumerator for all the keys. <see cref="ConcurrentDictionary{TKey,TValue}.Keys"/>
    /// is already a snapshot — no extra materialization required.
    /// </summary>
    public IEnumerable<object> Keys() => _cache.Keys;

    /// <summary>
    /// Gets the total count of items in cache.
    /// </summary>
    public int Count()
    {
        return _cache.Count;
    }

    /// <summary>
    /// Purges all cache items with normal priorities.
    /// </summary>
    public int PurgeNormalPriorities()
    {
        int removed = 0;
        foreach (var cacheItem in _cache)
        {
            if (cacheItem.Value.Priority != CacheItemPriority.Normal)
                continue;

            if (_cache.TryRemove(cacheItem.Key, out _))
            {
                _slidingTime.TryRemove(cacheItem.Key, out _);
                DisposeTimer(cacheItem.Key);
                removed++;
            }
        }
        return removed;
    }

    /// <summary>
    /// Determines whether the cache contains the specified key.
    /// </summary>
    public bool Contains<TKey>(TKey key)
    {
        return key is not null && _cache.ContainsKey(key);
    }

    /// <summary>
    /// Replaces the value for an existing key without resetting its expiry timer or
    /// sliding-time window. No-ops if the key is not present.
    /// </summary>
    public void Update<TKey, TValue>(TKey key, TValue value)
    {
        if (key is null) return;
        while (_cache.TryGetValue(key!, out var existing))
        {
            var replacement = new CacheItem(value!, existing.Priority, existing.RelativeExpiry);
            if (_cache.TryUpdate(key!, replacement, existing))
                break;
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Releases timers owned by this cache instance.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var kvp in _timers) kvp.Value.Dispose();
        _timers.Clear();
    }

    #endregion

    #region Private class helper

    private void Add<TKey, TValue>(TKey key, TValue value, TimeSpan timeSpan, CacheItemPriority priority, bool isSliding)
    {
        // Compound replace-and-reset: value, sliding window, and timer are all written
        // together so a re-Add fully supersedes the prior entry instead of refreshing the
        // stale value's TTL.
        lock (_addLock)
        {
            _cache[key!] = new CacheItem(value!, priority, isSliding ? timeSpan : (TimeSpan?)null);

            if (isSliding)
            {
                _slidingTime[key!] = new SlidingDetails(timeSpan, _timeProvider);
            }
            else
            {
                _slidingTime.TryRemove(key!, out _);
            }

            StartObserving(key!, timeSpan);
        }
    }

    private void StartObserving<TKey>(TKey key, TimeSpan timeSpan)
    {
        // Clamp to at least 1 ms to avoid a zero-delay timer firing before the caller returns.
        var delay = timeSpan.Ticks > 0 ? timeSpan : TimeSpan.FromMilliseconds(1);

        var timer = _timeProvider.CreateTimer(_ => TryPurgeItem(key!), null, delay, Timeout.InfiniteTimeSpan);

        // Swap in the new timer and dispose any previous one (re-observation after sliding check).
        _timers.AddOrUpdate(key!, timer, (_, existing) =>
        {
            existing.Dispose();
            return timer;
        });
    }

    private void TryPurgeItem<TKey>(TKey key)
    {
        if (_slidingTime.TryGetValue(key!, out var details))
        {
            if (!details.CanExpire(out TimeSpan tryAfter))
            {
                StartObserving(key, tryAfter);
                return;
            }
        }

        Remove(key);
    }

    private void DisposeTimer(object key)
    {
        if (_timers.TryRemove(key, out var timer))
        {
            timer.Dispose();
        }
    }

    #endregion
}
