using System;
using System.Collections.Generic;
using System.Runtime.Caching;

namespace ServiceConnect.Filters.MessageDeduplication.Persistors
{
    /// <summary>
    /// InMemory implementation of the persistor.
    /// Keeps processed message ids in ObjectCache
    /// </summary>
    public class MessageDeduplicationPersistorInMemory : IMessageDeduplicationPersistor
    {
        private static readonly ObjectCache Cache = MemoryCache.Default;
        private static readonly CacheItemPolicy CacheItemPolicy = new CacheItemPolicy { SlidingExpiration = new TimeSpan(0, 24, 0, 0, 0) };

        public bool GetMessageExists(Guid messageId)
        {
            return Cache.Contains(messageId.ToString());
        }

        public void Insert(Guid messageId, DateTime messageExpiry)
        {
            Cache.Add(messageId.ToString(), messageExpiry, CacheItemPolicy);
        }

        public void RemoveExpiredMessages(DateTime messageExpiry)
        {
            foreach (KeyValuePair<string, object> cacheItem in Cache)
            {
                if ((DateTime)cacheItem.Value < messageExpiry)
                {
                    Cache.Remove(cacheItem.Key);
                }
            }
        }
    }
}
