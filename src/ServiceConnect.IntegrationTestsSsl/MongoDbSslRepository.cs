using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using MongoDB.Driver;

namespace ServiceConnect.IntegrationTestsSsl
{
    public class MongoDbSslRepository
    {
        public IMongoDatabase MongoDatabase { get; }

        public MongoDbSslRepository(string connectionString, string databaseName)
        {
            var connectionParts = connectionString.Split(',');
            string nodes = string.Empty;
            string username = string.Empty;
            string password = string.Empty;
            string certPath = string.Empty;
            string userdb = string.Empty;
            string cert = string.Empty;
            string certPassword = string.Empty;

            foreach (string connectionPart in connectionParts)
            {
                var assignmentIndex = connectionPart.IndexOf('=');
                var nameValue = connectionPart.Substring(0, assignmentIndex);

                switch (nameValue.ToLower())
                {
                    case "nodes":
                        nodes = connectionPart.Substring(assignmentIndex + 1);
                        break;
                    case "userdb":
                        userdb = connectionPart.Substring(assignmentIndex + 1);
                        break;
                    case "username":
                        username = connectionPart.Substring(assignmentIndex + 1);
                        break;
                    case "password":
                        password = connectionPart.Substring(assignmentIndex + 1);
                        break;
                    case "certpath":
                        certPath = connectionPart.Substring(assignmentIndex + 1);
                        break;
                    case "cert":
                        cert = connectionPart.Substring(assignmentIndex + 1);
                        break;
                    case "certpassword":
                        certPassword = connectionPart.Substring(assignmentIndex + 1);
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

            MongoCredential credential = null;
            if (!string.IsNullOrEmpty(username))
            {
                string db = "admin";

                if (!string.IsNullOrEmpty(userdb))
                {
                    db = userdb;
                }

                credential = MongoCredential.CreateCredential(db, username, password);
            }

            var settings = new MongoClientSettings
            {
                UseTls = true,
                Credential = credential,
                ConnectionMode = ConnectionMode.Automatic,
                Servers = mongoNodes.Select(x => new MongoServerAddress(x)),
                SslSettings = new SslSettings
                {
                    ClientCertificates = certs,
                    ClientCertificateSelectionCallback = (sender, host, certificates, certificate, issuers) => certificates[0],
                    CheckCertificateRevocation = false
                }
            };

            var client = new MongoClient(settings);
            MongoDatabase = client.GetDatabase(databaseName);

        }
    }
}
