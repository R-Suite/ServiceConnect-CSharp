//Copyright (C) 2015  Timothy Watson, Jakub Pachansky

//This program is free software; you can redistribute it and/or
//modify it under the terms of the GNU General Public License
//as published by the Free Software Foundation; either version 2
//of the License, or (at your option) any later version.

//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//GNU General Public License for more details.

//You should have received a copy of the GNU General Public License
//along with this program; if not, write to the Free Software
//Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using MongoDB.Driver;
using MongoDB.Driver.Builders;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistance.MongoDbSsl
{
    public class MongoDBSslAggregatorPersistor : IAggregatorPersistor
    {
        private readonly MongoCollection<MongoDbSslData<object>> _collection;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="databaseName"></param>
        public MongoDBSslAggregatorPersistor(string connectionString, string databaseName)
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
                    MongoCredential.CreateCredential(db, username, password)
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

            var client = new MongoClient(settings);
            MongoServer server = client.GetServer();
            var mongoDatabase = server.GetDatabase(databaseName);
            _collection = mongoDatabase.GetCollection<MongoDbSslData<object>>("Aggregator");
        }

        public void InsertData(object data, string name)
        {
            _collection.Insert(new MongoDbSslData<object>
            {
                Name = name,
                Data = data,
                Version = 1
            });
        }

        public IList<object> GetData(string name)
        {
            return _collection.Find(Query<MongoDbSslData<object>>.EQ(x => x.Name, name)).Select(x => x.Data).ToList();
        }

        public void RemoveData(string name, Guid correlationsId)
        {
            _collection.Remove(
                Query.And(
                    Query<MongoDbSslData<object>>.EQ(x => x.Name, name),
                    Query<MongoDbSslData<Message>>.EQ(x => x.Data.CorrelationId, correlationsId)
                )
            );
        }
        
        public int Count(string name)
        {
            return Convert.ToInt32(_collection.Count(Query<MongoDbSslData<object>>.EQ(x => x.Name, name)));
        }
    }
}
