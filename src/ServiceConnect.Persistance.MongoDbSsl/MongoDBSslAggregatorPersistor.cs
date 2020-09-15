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
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistance.MongoDbSsl
{
    public class MongoDBSslAggregatorPersistor : IAggregatorPersistor
    {
        private readonly IMongoCollection<MongoDbSslData<object>> _collection;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="databaseName"></param>
        /// <param name="collectionName"></param>
        public MongoDBSslAggregatorPersistor(string connectionString, string databaseName, string collectionName)
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
            var mongoDatabase = client.GetDatabase(databaseName);
            _collection = mongoDatabase.GetCollection<MongoDbSslData<object>>(collectionName);
        }

        public void InsertData(object data, string name)
        {
            _collection.InsertOne(new MongoDbSslData<object>
            {
                Name = name,
                Data = data,
                Version = 1
            });
        }

        public IList<object> GetData(string name)
        {
            var filter = Builders<MongoDbSslData<object>>.Filter.Eq(_ => _.Name, name);
            return _collection.Find(filter).ToList().Select(x => x.Data).ToList();
        }

        public void RemoveData(string name, Guid correlationsId)
        {
            var filter = Builders<MongoDbSslData<object>>.Filter.Eq(_ => _.Name, name) &
                         Builders<MongoDbSslData<object>>.Filter.Eq("Data.CorrelationId", correlationsId);

            _collection.DeleteMany(filter);
        }
        
        public int Count(string name)
        {
            var filter = Builders<MongoDbSslData<object>>.Filter.Eq(_ => _.Name, name);
            return Convert.ToInt32(_collection.CountDocuments(filter));
        }
    }
}
