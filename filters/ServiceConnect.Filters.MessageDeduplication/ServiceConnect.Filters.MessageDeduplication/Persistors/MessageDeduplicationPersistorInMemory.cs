using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceConnect.Filters.MessageDeduplication.Persistors
{
    /// <summary>
    /// InMemory implementation of the persistor. Keeps processed message ids in a concurrent dictionary.
    /// </summary>
    public class MessageDeduplicationPersistorInMemory : IMessageDeduplicationPersistor
    {
        private static readonly ConcurrentDictionary<string, CacheItem> Cache = new ConcurrentDictionary<string, CacheItem>();

        public Task<bool> GetMessageExistsAsync(Guid messageId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Cache.ContainsKey(messageId.ToString()));
        }

        public Task InsertAsync(Guid messageId, DateTime messageExpiry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Cache.TryAdd(messageId.ToString(), new CacheItem { MessageExpiry = messageExpiry });
            return Task.CompletedTask;
        }

        public Task RemoveExpiredMessagesAsync(DateTime messageExpiry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (KeyValuePair<string, CacheItem> cacheItem in Cache)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cacheItem.Value.MessageExpiry < messageExpiry)
                {
                    Cache.TryRemove(cacheItem.Key, out _);
                }
            }
            return Task.CompletedTask;
        }
    }

    internal sealed class CacheItem
    {
        public object Value { get; set; }
        public DateTime MessageExpiry { get; set; }
    }
}
