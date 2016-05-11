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
using System.Linq.Expressions;
using System.Security.Cryptography.X509Certificates;
using MongoDB.Driver;
using MongoDB.Driver.Builders;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistance.MongoDbSsl
{
    /// <summary>
    /// MonoDb implementation of IProcessManagerFinder.
    /// </summary>
    public class MongoDbSslProcessManagerFinder : IProcessManagerFinder
    {
        private readonly MongoDatabase _mongoDatabase;

        public MongoDbSslProcessManagerFinder(string connectionString, string databaseName)
        {
            var connectionParts = connectionString.Split(',');
            string nodes = string.Empty;
            string username = string.Empty;
            string password = string.Empty;
            string certPath = string.Empty;
            string userdb = string.Empty;

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
                }
            }

            var mongoNodes = nodes.Split(';');

            List<X509Certificate> certs = null;
            if (!string.IsNullOrEmpty(certPath))
            {
                certs = new List<X509Certificate>
                {
                    new X509Certificate2(certPath)
                };
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
            _mongoDatabase = server.GetDatabase(databaseName);
        }

        /// <summary>
        /// Find existing instance of ProcessManager
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="mapper"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        public IPersistanceData<T> FindData<T>(IProcessManagerPropertyMapper mapper, Message message) where T : class, IProcessManagerData
        {
            var mapping = mapper.Mappings.FirstOrDefault(m => m.MessageType == message.GetType()) ??
                          mapper.Mappings.First(m => m.MessageType == typeof(Message));

            var collectionName = typeof(T).Name;
            MongoCollection<T> collection = _mongoDatabase.GetCollection<T>(collectionName);
            collection.CreateIndex("CorrelationId");

            object msgPropValue = mapping.MessageProp.Invoke(message);
            if (null == msgPropValue)
            {
                throw new ArgumentException("Message property expression evaluates to null");
            }

            //Left
            ParameterExpression pe = Expression.Parameter(typeof(MongoDbSslData<T>), "t");
            Expression left = Expression.Property(pe, typeof(MongoDbSslData<T>).GetProperty("Data"));
            foreach (var prop in mapping.PropertiesHierarchy.Reverse())
            {
                left = Expression.Property(left, left.Type, prop.Key);
            }

            //Right
            Expression right = Expression.Constant(msgPropValue, msgPropValue.GetType());

            Expression expression;

            try
            {
                expression = Expression.Equal(left, right);
            }
            catch (InvalidOperationException ex)
            {
                throw new Exception("Mapped incompatible types of ProcessManager Data and Message properties.", ex);
            }

            var lambda = Expression.Lambda<Func<MongoDbSslData<T>, bool>>(expression, pe);
            IMongoQuery query = Query<MongoDbSslData<T>>.Where(lambda);
            
            return collection.FindOneAs<MongoDbSslData<T>>(query);
        }

        /// <summary>
        /// Create new instance of ProcessManager
        /// When multiple threads try to create new ProcessManager instance, only the first one is allowed. 
        /// All subsequent threads will update data instead.
        /// </summary>
        /// <param name="data"></param>
        public void InsertData(IProcessManagerData data)
        {
            var collectionName = GetCollectionName(data);

            MongoCollection collection = _mongoDatabase.GetCollection(collectionName);
            collection.CreateIndex("CorrelationId");

            var mongoDbData = new MongoDbSslData<IProcessManagerData>
            {
                Data = data,
                Version = 1,
                Id = Guid.NewGuid()
            };

            collection.FindAndModify(Query.EQ("CorrelationId", mongoDbData.Data.CorrelationId), SortBy.Null, Update.Replace(mongoDbData), false, true);
        }

        /// <summary>
        /// Update data of existing ProcessManager. 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="persistanceData"></param>
        public void UpdateData<T>(IPersistanceData<T> persistanceData) where T : class, IProcessManagerData
        {
            var collectionName = GetCollectionName(persistanceData.Data);

            MongoCollection<T> collection = _mongoDatabase.GetCollection<T>(collectionName);
            collection.CreateIndex("CorrelationId");

            var versionData = (MongoDbSslData<T>)persistanceData;

            int currentVersion = versionData.Version;
            var query = Query.And(Query.EQ("Data.CorrelationId", versionData.Data.CorrelationId), Query.EQ("Version", currentVersion));
            versionData.Version += 1;
            var result = collection.FindAndModify(query, SortBy.Null, Update.Replace(versionData));

            if (result.ModifiedDocument == null)
                throw new ArgumentException(string.Format("Possible Concurrency Error. ProcessManagerData with CorrelationId {0} and Version {1} could not be updated.", versionData.Data.CorrelationId, versionData.Version));
        }

        /// <summary>
        /// Removes existing instance of ProcessManager from the database.
        /// </summary>
        /// <param name="persistanceData"></param>
        public void DeleteData<T>(IPersistanceData<T> persistanceData) where T : class, IProcessManagerData
        {
            var collectionName = GetCollectionName(persistanceData.Data);

            MongoCollection collection = _mongoDatabase.GetCollection(collectionName);
            collection.CreateIndex("CorrelationId");

            collection.Remove(Query.EQ("Data.CorrelationId", persistanceData.Data.CorrelationId));
        }

        private static string GetCollectionName<T>(T data) where T : class, IProcessManagerData
        {
            Type typeParameterType = data.GetType();
            var collectionName = typeParameterType.Name;
            return collectionName;
        }
    }
}
