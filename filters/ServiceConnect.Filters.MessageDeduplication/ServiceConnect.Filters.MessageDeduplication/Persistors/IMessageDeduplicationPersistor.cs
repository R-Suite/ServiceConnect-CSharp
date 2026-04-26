using System;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceConnect.Filters.MessageDeduplication.Persistors;

public interface IMessageDeduplicationPersistor
{
    /// <summary>
    /// Returns true if the message id exists in the relevant persistant storage.
    ///  => the message has been previously processed.
    /// </summary>
    Task<bool> GetMessageExistsAsync(Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a processed message into the relevant persistant storage.
    /// This happens immediately after the message has been processed.
    /// </summary>
    Task InsertAsync(Guid messageId, DateTime messageExpiry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all the expired message ids from the relevant persistant storage.
    /// This prevents the storage size from growing indefinitely.
    /// </summary>
    Task RemoveExpiredMessagesAsync(DateTime messageExpiry, CancellationToken cancellationToken = default);
}
