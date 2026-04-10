using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;

namespace ServiceConnect.Persistence.InMemory
{
    /// <summary>
    /// This library is based on http://ranahossain.blogspot.fr/2014/01/cache-provider-for-portable-class.html
    /// </summary>
    public class CacheProvider : ICacheProvider
    {
        public static CacheProvider Default { get; } = new CacheProvider();

        private readonly ConcurrentDictionary<object, CacheItem> _cache = new ConcurrentDictionary<object, CacheItem>();
        private readonly ConcurrentDictionary<object, SlidingDetails> _slidingTime = new ConcurrentDictionary<object, SlidingDetails>();

        #region Implementation of ICacheProvider

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
        public void Add<TKey, TValue>(TKey key, TValue value, DateTime absoluteExpiry, CacheItemPriority priority = CacheItemPriority.Normal)
        {
            if (absoluteExpiry < DateTime.Now)
            {
                return;
            }

            var diff = absoluteExpiry - DateTime.Now;
            Add(key, value, diff, priority, false);
        }

        /// <summary>
        /// Gets a value from the cache for specified key.
        /// </summary>
        public TValue Get<TKey, TValue>(TKey key)
        {
            try
            {
                var cacheItem = _cache[key!];

                if (cacheItem.RelativeExpiry.HasValue)
                    _slidingTime[key!].Slide();

                return (TValue)cacheItem.Value!;
            }
            catch (Exception)
            {
                return default(TValue)!;
            }
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

                KeyRemoved?.Invoke(key, new EventArgs());
            }
        }

        /// <summary>
        /// Clears the contents of the cache.
        /// </summary>
        public void Clear()
        {
            _cache.Clear();
            _slidingTime.Clear();
        }

        /// <summary>
        /// Gets an enumerator for keys of a specific type.
        /// </summary>
        public IEnumerable<TKey> Keys<TKey>()
        {
            return _cache.Keys.Where(k => k.GetType() == typeof(TKey)).Cast<TKey>().ToList();
        }

        /// <summary>
        /// Gets an enumerator for all the keys.
        /// </summary>
        public IEnumerable<object> Keys()
        {
            return _cache.Keys.ToList();
        }

        /// <summary>
        /// Gets the total count of items in cache.
        /// </summary>
        public int Count()
        {
            return _cache.Keys.Count;
        }

        /// <summary>
        /// Purges all cache items with normal priorities.
        /// </summary>
        public int PurgeNormalPriorities()
        {
            var keysToRemove = (from cacheItem in _cache where cacheItem.Value.Priority == CacheItemPriority.Normal select cacheItem.Key).ToList();
            return keysToRemove.Count(key => _cache.TryRemove(key, out _));
        }

        /// <summary>
        /// Determines whether [contains] [the specified key].
        /// </summary>
        public bool Contains(object key)
        {
            return _cache.ContainsKey(key);
        }

        #endregion

        #region Private class helper

        private void Add<TKey, TValue>(TKey key, TValue value, TimeSpan timeSpan, CacheItemPriority priority, bool isSliding)
        {
            _cache.TryAdd(key!, new CacheItem(value, priority, (isSliding ? timeSpan : (TimeSpan?)null)));

            if (isSliding)
            {
                _slidingTime.TryAdd(key!, new SlidingDetails(timeSpan));
            }

            StartObserving(key!, timeSpan);
        }

        private void StartObserving<TKey>(TKey key, TimeSpan timeSpan)
        {
            Observable.Timer(timeSpan)
                .Finally(() =>
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                })
                .Subscribe(x => TryPurgeItem(key!),
                exception =>
                {
                    // on error: Purge Failed with exception.Message
                });
        }

        private void TryPurgeItem<TKey>(TKey key)
        {
            if (_slidingTime.ContainsKey(key!))
            {
                if (!_slidingTime[key!].CanExpire(out TimeSpan tryAfter))
                {
                    StartObserving(key, tryAfter);
                    return;
                }
            }

            Remove(key);
        }

        #endregion
    }
}
