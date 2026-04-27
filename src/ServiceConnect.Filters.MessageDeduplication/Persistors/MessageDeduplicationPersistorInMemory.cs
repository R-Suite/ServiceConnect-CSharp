using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceConnect.Filters.MessageDeduplication.Persistors;

/// <summary>
/// InMemory implementation of the persistor. Keeps processed message ids in a concurrent dictionary
/// keyed by the raw Guid (not its string form) to avoid per-call allocation of a 36-char string.
/// </summary>
public class MessageDeduplicationPersistorInMemory : IMessageDeduplicationPersistor
{
    private static readonly ConcurrentDictionary<Guid, CacheItem> Cache = new();

    public Task<bool> GetMessageExistsAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Cache.ContainsKey(messageId));
    }

    public Task InsertAsync(Guid messageId, DateTime messageExpiry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Cache.TryAdd(messageId, new CacheItem { MessageExpiry = messageExpiry });
        return Task.CompletedTask;
    }

    public Task RemoveExpiredMessagesAsync(DateTime messageExpiry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (KeyValuePair<Guid, CacheItem> cacheItem in Cache)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cacheItem.Value.MessageExpiry < messageExpiry)
            {
                Cache.TryRemove(cacheItem.Key, out _);
            }
        }
        return Task.CompletedTask;
    }

    private sealed class CacheItem
    {
        public DateTime MessageExpiry { get; set; }
    }
}
