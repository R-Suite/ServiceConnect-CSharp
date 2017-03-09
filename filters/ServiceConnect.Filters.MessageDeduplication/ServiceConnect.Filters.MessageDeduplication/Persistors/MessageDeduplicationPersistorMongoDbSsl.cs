using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using Common.Logging;
using MongoDB.Driver;

namespace ServiceConnect.Filters.MessageDeduplication.Persistors
{
    public class MessageDeduplicationPersistorMongoDbSsl : IMessageDeduplicationPersistor
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(MessageDeduplicationPersistorMongoDbSsl));
        private readonly IMongoCollection<ProcessedMessage> _collection;

        public MessageDeduplicationPersistorMongoDbSsl()
        {
            var filterSettings = DeduplicationFilterSettings.Instance;
            var connectionParts = filterSettings.ConnectionStringMongoDb.Split(',');
            string nodes = string.Empty;
            string username = string.Empty;
            string password = string.Empty;
            string certPath = string.Empty;
            string userdb = string.Empty;
            string cert = string.Empty;
            string certPassword = string.Empty;

            foreach (string connectionPart in connectionParts)
            {
                var nameValue = connectionPart.Split('=');
                switch (nameValue[0].ToLower())
                {
                    case "nodes":
                        nodes = nameValue[1];
                        break;
                    case "userdb":
                        userdb = nameValue[1];
                        break;
                    case "username":
                        username = nameValue[1];
                        break;
                    case "password":
                        password = nameValue[1];
                        break;
                    case "certpath":
                        certPath = nameValue[1];
                        break;
                    case "cert":
                        cert = nameValue[1];
                        break;
                    case "certpassword":
                        certPassword = nameValue[1];
                        break;
                }
            }

            var mongoNodes = nodes.Split(';');

            List<X509Certificate> certs = null;
            if (!string.IsNullOrEmpty(certPath))
            {
                if (string.IsNullOrEmpty(certPassword))
                {
                    certs = new List<X509Certificate>
                    {
                        new X509Certificate2(certPath)
                    };
                }
                else
                {
                    certs = new List<X509Certificate>
                    {
                        new X509Certificate2(certPath, certPassword)
                    };
                }

            }

            if (!string.IsNullOrEmpty(cert))
            {
                if (string.IsNullOrEmpty(certPassword))
                {
                    certs = new List<X509Certificate>
                    {
                        new X509Certificate2(Convert.FromBase64String(cert))
                    };
                }
                else
                {
                    certs = new List<X509Certificate>
                    {
                        new X509Certificate2(Convert.FromBase64String(cert), certPassword)
                    };
                }
            }

            List<MongoCredential> credentials = null;
            if (!string.IsNullOrEmpty(username))
            {
                string db = "admin";

                if (!string.IsNullOrEmpty(userdb))
                {
                    db = userdb;
                }

                credentials = new List<MongoCredential>
                {
                    MongoCredential.CreateCredential(db, username, password),
                };
            }

            var settings = new MongoClientSettings
            {
                UseSsl = true,
                Credentials = credentials,
                ConnectionMode = ConnectionMode.Automatic,
                Servers = mongoNodes.Select(x => new MongoServerAddress(x)),
                SslSettings = new SslSettings
                {
                    ClientCertificates = certs,
                    ClientCertificateSelectionCallback = (sender, host, certificates, certificate, issuers) => certificates[0],
                    CheckCertificateRevocation = false
                }
            };



            var mongoClient = new MongoClient(settings);
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

        public void Insert(Guid messageId, DateTime messageExpiry)
        {
            try
            {
                _collection.InsertOne(new ProcessedMessage
                {
                    Id = messageId,
                    ExpiryDateTime = messageExpiry
                });
            }
            catch (Exception ex)
            {
                Logger.Fatal("Error inserting into ProcessedMessage collection", ex);
            }
        }

        public void RemoveExpiredMessages(DateTime messageExpiry)
        {
            try
            {
                _collection.DeleteMany(i => i.ExpiryDateTime < messageExpiry);
            }
            catch (Exception ex)
            {
                Logger.Fatal("Error cleaning up expired ProcessedMessages", ex);
            }
        }
    }
}
