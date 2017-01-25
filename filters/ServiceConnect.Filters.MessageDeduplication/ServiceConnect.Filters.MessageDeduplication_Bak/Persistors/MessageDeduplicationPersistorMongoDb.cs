using System;
using System.Reflection;
using Common.Logging;
using MongoDB.Driver;

namespace ServiceConnect.Filters.MessageDeduplication.Persistors
{
    public class MessageDeduplicationPersistorMongoDb : IMessageDeduplicationPersistor
    {
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly IMongoCollection<ProcessedMessage> _collection;

        public MessageDeduplicationPersistorMongoDb()
        {
            var settings = DeduplicationFilterSettings.Instance;
            var mongoClient = new MongoClient(settings.ConnectionStringMongoDb);
            var mongoDatabase = mongoClient.GetDatabase(settings.DatabaseNameMongoDb);
            _collection = mongoDatabase.GetCollection<ProcessedMessage>(settings.CollectionNameMongoDb);
        }

        public bool GetMessageExists(Guid messageId)
        {
            IAsyncCursor<ProcessedMessage> result = _collection.FindAsync(i=>i.Id == messageId).Result;
            return result.Any();
        }

        public async void Insert(Guid messageId, DateTime messagExpiry)
        {
            try
            {
                await _collection.InsertOneAsync(new ProcessedMessage
                {
                    Id = messageId,
                    ExpiryDateTime = messagExpiry
                });
            }
            catch (Exception ex)
            {
                Logger.Fatal("Error inserting into ProcessedMessage collection", ex);
            }
        }

        public void RemoveExpiredMessages(DateTime messagExpiry)
        {
            try
            {
                _collection.DeleteMany(i => i.ExpiryDateTime < messagExpiry);
            }
            catch (Exception ex)
            {
                Logger.Fatal("Error cleaning up expired ProcessedMessages", ex);
            }
        }
    }
}