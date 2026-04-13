using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;

namespace ServiceConnect.Filters.MessageDeduplication.Persistors
{
    public class MessageDeduplicationPersistorMongoDb : IMessageDeduplicationPersistor
    {
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

                clientSettings.UseTls = true;
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

            // Ensure indexes (fire-and-forget during construction is existing behavior).
            _collection.Indexes.CreateOneAsync(
                new CreateIndexModel<ProcessedMessage>(Builders<ProcessedMessage>.IndexKeys.Ascending(_ => _.Id)));
            _collection.Indexes.CreateOneAsync(
                new CreateIndexModel<ProcessedMessage>(Builders<ProcessedMessage>.IndexKeys.Ascending(_ => _.ExpiryDateTime)));
        }

        public async Task<bool> GetMessageExistsAsync(Guid messageId, CancellationToken cancellationToken = default)
        {
            var found = await _collection.Find(i => i.Id == messageId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            return found != null;
        }

        public Task InsertAsync(Guid messageId, DateTime messageExpiry, CancellationToken cancellationToken = default)
        {
            return _collection.InsertOneAsync(
                new ProcessedMessage
                {
                    Id = messageId,
                    ExpiryDateTime = messageExpiry
                },
                options: null,
                cancellationToken: cancellationToken);
        }

        public Task RemoveExpiredMessagesAsync(DateTime messageExpiry, CancellationToken cancellationToken = default)
        {
            return _collection.DeleteManyAsync(i => i.ExpiryDateTime < messageExpiry, cancellationToken);
        }
    }
}
