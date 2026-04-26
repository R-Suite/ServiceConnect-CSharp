using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;

namespace ServiceConnect.Filters.MessageDeduplication.Persistors;

public class MessageDeduplicationPersistorMongoDb : IMessageDeduplicationPersistor
{
    private readonly IMongoCollection<ProcessedMessage> _collection;

    public MessageDeduplicationPersistorMongoDb(DeduplicationFilterSettings settings)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        var url = new MongoUrl(settings.ConnectionStringMongoDb);
        var clientSettings = MongoClientSettings.FromUrl(url);

        if (!string.IsNullOrEmpty(settings.MongoDbCertPath) ||
            !string.IsNullOrEmpty(settings.MongoDbCertBase64))
        {
            X509Certificate2 cert;
            if (!string.IsNullOrEmpty(settings.MongoDbCertPath))
            {
                cert = string.IsNullOrEmpty(settings.MongoDbCertPassphrase)
                    ? X509CertificateLoader.LoadCertificateFromFile(settings.MongoDbCertPath)
                    : X509CertificateLoader.LoadPkcs12FromFile(settings.MongoDbCertPath, settings.MongoDbCertPassphrase);
            }
            else
            {
                var certBytes = Convert.FromBase64String(settings.MongoDbCertBase64!);
                cert = string.IsNullOrEmpty(settings.MongoDbCertPassphrase)
                    ? X509CertificateLoader.LoadCertificate(certBytes)
                    : X509CertificateLoader.LoadPkcs12(certBytes, settings.MongoDbCertPassphrase);
            }

            clientSettings.UseTls = true;
            clientSettings.SslSettings = new SslSettings
            {
                ClientCertificates = [cert],
                ClientCertificateSelectionCallback = (sender, host, certificates, certificate, issuers) => certificates[0],
                CheckCertificateRevocation = true
            };
        }

        var mongoClient = new MongoClient(clientSettings);
        var mongoDatabase = mongoClient.GetDatabase(settings.DatabaseNameMongoDb);
        _collection = mongoDatabase.GetCollection<ProcessedMessage>(settings.CollectionNameMongoDb);

        // Ensure indexes synchronously at construction. A failure here
        // must surface so the caller can react rather than silently proceed
        // without indexes.
        _collection.Indexes.CreateOne(
            new CreateIndexModel<ProcessedMessage>(Builders<ProcessedMessage>.IndexKeys.Ascending(_ => _.Id)));
        _collection.Indexes.CreateOne(
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
            new ProcessedMessage { Id = messageId, ExpiryDateTime = messageExpiry },
            options: null,
            cancellationToken: cancellationToken);
    }

    public Task RemoveExpiredMessagesAsync(DateTime messageExpiry, CancellationToken cancellationToken = default)
    {
        return _collection.DeleteManyAsync(i => i.ExpiryDateTime < messageExpiry, cancellationToken);
    }
}
