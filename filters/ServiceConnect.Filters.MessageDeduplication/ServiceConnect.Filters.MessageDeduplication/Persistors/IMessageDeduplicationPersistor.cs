using System;

namespace ServiceConnect.Filters.MessageDeduplication.Persistors
{
    public interface IMessageDeduplicationPersistor
    {
        /// <summary>
        /// Returns true if the message id exists in the relevant persistant storage.
        ///  => the message has been previously processed.
        /// </summary>
        /// <param name="messageId"></param>
        /// <returns></returns>
        bool GetMessageExists(Guid messageId);

        /// <summary>
        /// Inserts a proccessed message into the relevant persistant storage.
        /// This happens immediately after the message has been processed.
        /// </summary>
        /// <param name="messageId"></param>
        /// <param name="messagExpiry"></param>
        void Insert(Guid messageId, DateTime messagExpiry);

        /// <summary>
        /// Removes all the expired message ids from the relevant persistant storage.
        /// This prevents the storage size to grow indefinitely.
        /// </summary>
        /// <param name="messagExpiry"></param>
        void RemoveExpiredMessages(DateTime messagExpiry);
    }
}