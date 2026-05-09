using System.Collections.Concurrent;

namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Provides an in-memory cache with absolute and sliding expiration support.
/// </summary>
/// <remarks>
/// Initializes a new <see cref="CacheProvider"/> using the supplied clock.
/// </remarks>
public sealed class CacheProvider(TimeProvider? timeProvider = null) : ICacheProvider, IKeyValueStore, IDisposable
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<object, CacheItem> _cache = new();
    private readonly ConcurrentDictionary<object, SlidingDetails> _slidingTime = new();
    private readonly ConcurrentDictionary<object, ITimer> _timers = new();
    // Per-key generation counter. Each Add for a key bumps it; the timer callback
    // captures the value at registration and skips eviction if the captured value
    // doesn't match the current generation — closing the re-Add-during-callback
    // race where an in-flight TryPurgeItem could evict the new value via Remove(key).
    // ITimer.Dispose does not wait for in-flight callbacks, so a callback that
    // already read _slidingTime[key] before the new Add overwrote it would otherwise
    // observe the old ExpireAt, return CanExpire==true, and remove the new value.
    // ConcurrentDictionary's AddOrUpdate atomicity ensures the bump and the timer
    // installation are observed together by stale callbacks via TryGetValue.
    private readonly ConcurrentDictionary<object, long> _generations = new();
    // Serializes compound Add operations so the value swap, sliding window reset, and
    // timer replacement are observed together. Without it, a re-Add after the original
    // TryAdd retained the stale value but StartObserving installed a fresh timer —
    // effectively extending the stale value's TTL.
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _addLock = new();
#else
    private readonly object _addLock = new();
#endif
    private int _disposed;

    #region Implementation of ICacheProvider

    /// <summary>
    /// Occurs after an entry is removed from the cache.
    /// </summary>
    public event EventHandler<KeyRemovedEventArgs>? KeyRemoved;

    /// <summary>
    /// Add a value to the cache with a relative expiry time, e.g 10 minutes.
    /// </summary>
    public void Add<TKey, TValue>(TKey key, TValue value, TimeSpan slidingExpiry, CacheItemPriority priority = CacheItemPriority.Normal)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Add(key, value, slidingExpiry, priority, true);
    }

    /// <summary>
    /// Add a value to the cache with an absolute time, e.g. 01/01/2020.
    /// </summary>
    public void Add<TKey, TValue>(TKey key, TValue value, DateTimeOffset absoluteExpiry, CacheItemPriority priority = CacheItemPriority.Normal)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Single clock read — defensive against a non-monotonic test TimeProvider where
        // a second GetUtcNow() call could observe an earlier time, producing a negative diff.
        var now = _timeProvider.GetUtcNow();
        if (absoluteExpiry < now)
        {
            throw new ArgumentOutOfRangeException(nameof(absoluteExpiry), "Absolute expiry must be in the future.");
        }

        var diff = absoluteExpiry - now;
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
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
    /// Tries to get a value from the cache for the specified key.
    /// Returns <see langword="true"/> and writes the stored value (possibly <see langword="null"/>)
    /// to <paramref name="value"/> if the key is present; returns <see langword="false"/> otherwise.
    /// Replaces pre-v8 <c>Get</c>, which returned <c>default!</c> on miss and made "absent" and
    /// "present with null" indistinguishable at the call site.
    /// </summary>
    public bool TryGet<TKey, TValue>(TKey key, out TValue? value)
    {
        if (!_cache.TryGetValue(key!, out var cacheItem))
        {
            value = default;
            return false;
        }

        if (cacheItem.RelativeExpiry.HasValue && _slidingTime.TryGetValue(key!, out var sliding))
        {
            sliding.Slide();
        }

        value = (TValue?)cacheItem.Value;
        return true;
    }

    /// <summary>
    /// Remove a value from the cache for specified key.
    /// </summary>
    public void Remove<TKey>(TKey key)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Equals(key, null))
        {
            return;
        }

        var removed = _cache.TryRemove(key!, out _);
        _slidingTime.TryRemove(key!, out _);
        DisposeTimer(key!);

        // Fire KeyRemoved only when the key was actually present so subscribers
        // never observe removal events for keys that were never in the cache.
        if (removed)
        {
            KeyRemoved?.Invoke(this, new KeyRemovedEventArgs(key!));
        }
    }

    /// <summary>
    /// Clears the contents of the cache.
    /// </summary>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        // Snapshot keys before clearing so subscribers see a KeyRemoved event for every
        // entry that was present at this point. Entries added between this snapshot and
        // the _cache.Clear() call below are silently dropped without a KeyRemoved event —
        // callers needing strict cross-thread consistency must synchronise externally.
        var removedKeys = _cache.Keys.ToList();

        _cache.Clear();
        _slidingTime.Clear();

        foreach (var kvp in _timers)
        {
            kvp.Value.Dispose();
        }
        _timers.Clear();

        if (KeyRemoved is null)
        {
            return;
        }

        foreach (var key in removedKeys)
        {
            KeyRemoved.Invoke(this, new KeyRemovedEventArgs(key));
        }
    }

    /// <summary>
    /// Gets an enumerator for keys assignable to TKey, including derived types and
    /// interface implementations. Streams the ConcurrentDictionary snapshot so callers
    /// that bail early avoid the full filtered-materialization cost.
    /// </summary>
    public IEnumerable<TKey> Keys<TKey>()
    {
        var typeOfKey = typeof(TKey);
        foreach (var k in _cache.Keys)
        {
            if (typeOfKey.IsAssignableFrom(k.GetType()))
            {
                yield return (TKey)k;
            }
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        int removed = 0;
        foreach (var cacheItem in _cache)
        {
            if (cacheItem.Value.Priority != CacheItemPriority.Normal)
            {
                continue;
            }

            // KVP-overload TryRemove succeeds only when the value reference still
            // matches the one observed during the scan. A concurrent re-Add that
            // upgraded the priority replaces the dictionary slot with a fresh
            // CacheItem reference, so this remove correctly fails and leaves the
            // upgraded entry in place.
            if (_cache.TryRemove(cacheItem))
            {
                _slidingTime.TryRemove(cacheItem.Key, out _);
                DisposeTimer(cacheItem.Key);
                removed++;
                KeyRemoved?.Invoke(this, new KeyRemovedEventArgs(cacheItem.Key));
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
    /// sliding-time window. Throws <see cref="KeyNotFoundException"/> if the key is absent.
    /// </summary>
    public void Update<TKey, TValue>(TKey key, TValue value)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (key is null)
        {
            return;
        }

        while (true)
        {
            if (!_cache.TryGetValue(key!, out var existing))
            {
                // Either the key was never present, or another thread removed it during
                // the CAS retry. Either way the caller's optimistic-update contract is
                // violated — throw so the caller can react deterministically.
                // Pre-Phase-10 this was a silent no-op — which let aggregator's optimistic-
                // concurrency loop advance Version against a phantom row.
                throw new KeyNotFoundException(
                    $"Cannot Update key '{key}' — key not present. Use Add to insert new keys.");
            }

            var replacement = new CacheItem(value!, existing.Priority, existing.RelativeExpiry);
            if (_cache.TryUpdate(key!, replacement, existing))
            {
                return;
            }
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Releases timers owned by this cache instance.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var kvp in _timers)
        {
            kvp.Value.Dispose();
        }

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

            // Bump the generation BEFORE installing the new timer. The timer callback
            // captures this value; a stale callback from the prior generation will see
            // a mismatch and skip eviction.
            var generation = _generations.AddOrUpdate(key!, 1L, (_, prior) => prior + 1L);
            StartObserving(key!, timeSpan, generation);
        }
    }

    private void StartObserving<TKey>(TKey key, TimeSpan timeSpan, long generation)
    {
        // Clamp to at least 1 ms to avoid a zero-delay timer firing before the caller returns.
        var delay = timeSpan.Ticks > 0 ? timeSpan : TimeSpan.FromMilliseconds(1);

        var timer = _timeProvider.CreateTimer(_ => TryPurgeItem(key!, generation), null, delay, Timeout.InfiniteTimeSpan);

        // Swap in the new timer and dispose any previous one (re-observation after sliding check).
        _timers.AddOrUpdate(key!, timer, (_, existing) =>
        {
            existing.Dispose();
            return timer;
        });
    }

    private void TryPurgeItem<TKey>(TKey key, long generation)
    {
        // Fast-fail: if the generation has been bumped (a new Add happened for this key),
        // this callback is stale and must NOT evict. ITimer.Dispose() doesn't wait for
        // callbacks, so a stale TryPurgeItem can be running on a disposed timer; the
        // generation check is the load-bearing guard against evicting the new value.
        if (!_generations.TryGetValue(key!, out var current) || current != generation)
        {
            return;
        }

        if (_slidingTime.TryGetValue(key!, out var details))
        {
            if (!details.CanExpire(out TimeSpan tryAfter))
            {
                // Don't re-install a timer that captures 'this' if dispose already ran.
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                // Re-observation within the same Add cycle — keep the same generation so
                // a future Add bump invalidates this re-installed callback as well.
                StartObserving(key, tryAfter, generation);
                return;
            }
        }

        try
        {
            Remove(key);
        }
        catch (ObjectDisposedException)
        {
            // The cache was disposed while a timer callback was already in flight.
            // Swallow — the dispose path takes ownership of cleanup; this best-effort
            // invocation is redundant.
        }
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
