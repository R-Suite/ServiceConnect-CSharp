using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ServiceConnect.Filters.MessageDeduplication.Persistors
{
    /// <summary>
    /// InMemory implementation of the persistor.
    /// Keeps processed message ids in ObjectCache
    /// </summary>
    public class MessageDeduplicationPersistorInMemory : IMessageDeduplicationPersistor
    {
        private static readonly ConcurrentDictionary<string, CacheItem> Cache = new ConcurrentDictionary<string, CacheItem>();

        public bool GetMessageExists(Guid messageId)
        {
            return Cache.ContainsKey(messageId.ToString());
        }

        public void Insert(Guid messageId, DateTime messageExpiry)
        {
            Cache.TryAdd(messageId.ToString(), new CacheItem { MessageExpiry = messageExpiry });
        }

        public void RemoveExpiredMessages(DateTime messageExpiry)
        {
            foreach (KeyValuePair<string, CacheItem> cacheItem in Cache)
            {
                if (cacheItem.Value.MessageExpiry < messageExpiry)
                {
                    CacheItem ci;
                    Cache.TryRemove(cacheItem.Key, out ci);
                }
            }
        }
    }

    internal sealed class CacheItem
    {
        public object Value { get; set; }
        public DateTime MessageExpiry { get; set; }
    }
}
