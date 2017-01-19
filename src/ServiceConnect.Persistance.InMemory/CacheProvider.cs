using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;

namespace ServiceConnect.Persistance.InMemory
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

        public event EventHandler KeyRemoved;

        /// <summary>
        /// Add a value to the cache with a relative expiry time, e.g 10 minutes.
        /// </summary>
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <typeparam name="TValue">The type of the value.</typeparam>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <param name="slidingExpiry">The sliding time when the key value pair should expire and be purged from the cache.</param>
        /// <param name="priority">Normal priority will be purged on low memory warning.</param>
        public void Add<TKey, TValue>(TKey key, TValue value, TimeSpan slidingExpiry, CacheItemPriority priority = CacheItemPriority.Normal)
        {
            Add(key, value, slidingExpiry, priority, true);
        }

        /// <summary>
        /// Add a value to the cache with an absolute time, e.g. 01/01/2020.
        /// </summary>
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <typeparam name="TValue">The type of the value.</typeparam>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <param name="absoluteExpiry">The absolute date time when the cache should expire and be purged the value.</param>
        /// <param name="priority">Normal priority will be purged on low memory warning.</param>
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
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <typeparam name="TValue">The type of the value.</typeparam>
        /// <param name="key">The key.</param>
        /// <returns>
        /// If the key exists in the cache then the value is returned, if the key does not exist then null is returned.
        /// </returns>
        public TValue Get<TKey, TValue>(TKey key)
        {
            try
            {
                var cacheItem = _cache[key];

                if (cacheItem.RelativeExpiry.HasValue)
                    _slidingTime[key].Slide();

                return (TValue)cacheItem.Value;
            }
            catch (Exception)
            {
                return default(TValue);
            }
        }

        /// <summary>
        /// Remove a value from the cache for specified key.
        /// </summary>
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <param name="key">The key.</param>
        public void Remove<TKey>(TKey key)
        {
            if (!Equals(key, null))
            {
                CacheItem cacheItem;
                _cache.TryRemove(key, out cacheItem);

                SlidingDetails slidingDetails;
                _slidingTime.TryRemove(key, out slidingDetails);

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
        /// <typeparam name="TKey">The type of the key.</typeparam>
        /// <returns>
        /// Returns an enumerator for keys of a specific type.
        /// </returns>
        public IEnumerable<TKey> Keys<TKey>()
        {
            return _cache.Keys.Where(k => k.GetType() == typeof(TKey)).Cast<TKey>().ToList();
        }

        /// <summary>
        /// Gets an enumerator for all the keys
        /// </summary>
        /// <returns>
        /// Returns an enumerator for all the keys.
        /// </returns>
        public IEnumerable<object> Keys()
        {
            return _cache.Keys.ToList();
        }

        /// <summary>
        /// Gets the total count of items in cache
        /// </summary>
        /// <returns>
        /// -1 if failed
        /// </returns>
        public int Count()
        {
            return _cache.Keys.Count;
        }

        /// <summary>
        /// Purges all cache item with normal priorities.
        /// </summary>
        /// <returns>
        /// Number of items removed (-1 if failed)
        /// </returns>
        public int PurgeNormalPriorities()
        {
            var keysToRemove = (from cacheItem in _cache where cacheItem.Value.Priority == CacheItemPriority.Normal select cacheItem.Key).ToList();

            CacheItem item;
            return keysToRemove.Count(key => _cache.TryRemove(key, out item));

        }

        /// <summary>
        /// Determines whether [contains] [the specified key].
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns></returns>
        public bool Contains(object key)
        {
            return this._cache.ContainsKey(key);
        }

        #endregion

        #region Private class helper

        private void Add<TKey, TValue>(TKey key, TValue value, TimeSpan timeSpan, CacheItemPriority priority, bool isSliding)
        {
            // add to cache
            _cache.TryAdd(key, new CacheItem(value, priority, ((isSliding) ? timeSpan : (TimeSpan?)null)));

            // keep sliding track
            if (isSliding)
            {
                _slidingTime.TryAdd(key, new SlidingDetails(timeSpan));
            }

            StartObserving(key, timeSpan);
        }

        private void StartObserving<TKey>(TKey key, TimeSpan timeSpan)
        {
            Observable.Timer(timeSpan)
                .Finally(() =>
                {
                    // on finished
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                })
                // on next
                .Subscribe(x => TryPurgeItem(key),
                exception =>
                {
                    // on error: Purge Failed with exception.Message
                });
        }

        private void TryPurgeItem<TKey>(TKey key)
        {
            if (_slidingTime.ContainsKey(key))
            {
                TimeSpan tryAfter;
                if (!_slidingTime[key].CanExpire(out tryAfter))
                {
                    // restart observing
                    StartObserving(key, tryAfter);
                    return;
                }
            }

            Remove(key);
        }

        #endregion
    }
}
