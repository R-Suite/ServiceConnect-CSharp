using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Caching;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Persistance.InMemory
{
    public class InMemoryAggregatorPersistor : IAggregatorPersistor
    {
        private static readonly ObjectCache Cache = MemoryCache.Default;
        readonly CacheItemPolicy _policy = new CacheItemPolicy { Priority = CacheItemPriority.Default };
        private readonly object _memoryCacheLock = new object();

        /// <summary>
        /// Constructor (parameters not used but needed)
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="databaseName"></param>
        public InMemoryAggregatorPersistor(string connectionString, string databaseName)
        { }

        public void InsertData(object data, string name)
        {
            lock (_memoryCacheLock)
            {
                if (Cache.Contains(name))
                {
                    var cacheItem = Cache.GetCacheItem(name);
                    ((IList<MemoryData<object>>)cacheItem.Value).Add(new MemoryData<object>
                    {
                        Data = data
                    });
                }
                else
                {
                    Cache.Add(new CacheItem(name, new List<MemoryData<object>> { 
                        new MemoryData<object>
                        {
                            Data = data
                        } 
                    }), _policy);
                }
            }
        }

        public IList<object> GetData(string name)
        {
            lock (_memoryCacheLock)
            {
                if (Cache.Contains(name))
                {
                    var cacheItem = Cache.GetCacheItem(name);
                    return ((List<MemoryData<object>>)cacheItem.Value).Select(x => x.Data).ToList();
                }
                return new List<object>();
            }
        }

        public void RemoveAll(string name)
        {
            lock (_memoryCacheLock)
            {
                if (Cache.Contains(name))
                {
                    Cache.Remove(name);
                }
            }
        }

        public int Count(string name)
        {
            lock (_memoryCacheLock)
            {
                if (Cache.Contains(name))
                {
                    var cacheItem = Cache.GetCacheItem(name);
                    return ((List<MemoryData<object>>)cacheItem.Value).Count;
                }
                return 0;
            }
        }
    }
}
