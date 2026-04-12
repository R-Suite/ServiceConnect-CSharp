using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using Common.Logging;
using MongoDB.Driver;

namespace ServiceConnect.Filters.MessageDeduplication.Persistors
{
    public class MessageDeduplicationPersistorMongoDb : IMessageDeduplicationPersistor
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(MessageDeduplicationPersistorMongoDb));
        private readonly IMongoCollection<ProcessedMessage> _collection;

        public MessageDeduplicationPersistorMongoDb()
        {
            var filterSettings = DeduplicationFilterSettings.Instance;

            var url = new MongoUrl(filterSettings.ConnectionStringMongoDb);
            var clientSettings = MongoClientSettings.FromUrl(url);

            if (!string.IsNullOrEmpty(filterSettings.MongoDbCertPath) ||
                !string.IsNullOrEmpty(filterSettings.MongoDbCertBase64))
            {
                X509Certificate2 cert;
                if (!string.IsNullOrEmpty(filterSettings.MongoDbCertPath))
                {
                    cert = string.IsNullOrEmpty(filterSettings.MongoDbCertPassphrase)
                        ? new X509Certificate2(filterSettings.MongoDbCertPath)
                        : new X509Certificate2(filterSettings.MongoDbCertPath, filterSettings.MongoDbCertPassphrase);
                }
                else
                {
                    var certBytes = Convert.FromBase64String(filterSettings.MongoDbCertBase64);
                    cert = string.IsNullOrEmpty(filterSettings.MongoDbCertPassphrase)
                        ? new X509Certificate2(certBytes)
                        : new X509Certificate2(certBytes, filterSettings.MongoDbCertPassphrase);
                }

                clientSettings.UseSsl = true;
                clientSettings.SslSettings = new SslSettings
                {
                    ClientCertificates = new List<X509Certificate> { cert },
                    ClientCertificateSelectionCallback = (sender, host, certificates, certificate, issuers) => certificates[0],
                    CheckCertificateRevocation = true
                };
            }

            var mongoClient = new MongoClient(clientSettings);
            var mongoDatabase = mongoClient.GetDatabase(filterSettings.DatabaseNameMongoDb);
            _collection = mongoDatabase.GetCollection<ProcessedMessage>(filterSettings.CollectionNameMongoDb);
            _collection.Indexes.CreateOneAsync(Builders<ProcessedMessage>.IndexKeys.Ascending(_ => _.Id));
            _collection.Indexes.CreateOneAsync(Builders<ProcessedMessage>.IndexKeys.Ascending(_ => _.ExpiryDateTime));
        }

        public bool GetMessageExists(Guid messageId)
        {
            IAsyncCursor<ProcessedMessage> result = _collection.FindAsync(i => i.Id == messageId).Result;
            return result.Any();
        }

        public void Insert(Guid messageId, DateTime messagExpiry)
        {
            try
            {
                _collection.InsertOne(new ProcessedMessage
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
