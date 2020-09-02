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
using ServiceConnect.Interfaces;
using System.Reflection;

namespace ServiceConnect.Persistance.MongoDbSsl
{
    /// <summary>
    /// MonoDb implementation of IProcessManagerFinder.
    /// </summary>
    public class MongoDbSslProcessManagerFinder : IProcessManagerFinder
    {
        private readonly IMongoDatabase _mongoDatabase;
        private const string TimeoutsCollectionName = "Timeouts";

        public MongoDbSslProcessManagerFinder(string connectionString, string databaseName)
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
            _mongoDatabase = client.GetDatabase(databaseName);
        }

        public event TimeoutInsertedDelegate TimeoutInserted;

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
            IMongoCollection<MongoDbSslData<T>> collection = _mongoDatabase.GetCollection<MongoDbSslData<T>>(collectionName);
            var indexOptions = new CreateIndexOptions();
            var indexKeys = Builders<MongoDbSslData<T>>.IndexKeys.Ascending("Data.CorrelationId");
            var indexModel = new CreateIndexModel<MongoDbSslData<T>>(indexKeys, indexOptions);
            collection.Indexes.CreateOne(indexModel);

            object msgPropValue = null;

            try
            {
                msgPropValue = mapping.MessageProp.Invoke(message);
            }
            catch
            {
                return null;
            }

            if (null == msgPropValue)
            {
                throw new ArgumentException("Message property expression evaluates to null");
            }

            //Left
            ParameterExpression pe = Expression.Parameter(typeof(MongoDbSslData<T>), "t");
            Expression left = Expression.Property(pe, typeof(MongoDbSslData<T>).GetTypeInfo().GetProperty("Data"));
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

            Expression<Func<MongoDbSslData<T>, bool>> lambda = Expression.Lambda<Func<MongoDbSslData<T>, bool>>(expression, pe);

            return collection.AsQueryable().FirstOrDefault(lambda);
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

            IMongoCollection<MongoDbSslData<IProcessManagerData>> collection = _mongoDatabase.GetCollection<MongoDbSslData<IProcessManagerData>>(collectionName);
            var indexOptions = new CreateIndexOptions();
            var indexKeys = Builders<MongoDbSslData<IProcessManagerData>>.IndexKeys.Ascending("Data.CorrelationId");
            var indexModel = new CreateIndexModel<MongoDbSslData<IProcessManagerData>>(indexKeys, indexOptions);
            collection.Indexes.CreateOne(indexModel);

            var mongoDbData = new MongoDbSslData<IProcessManagerData>
            {
                Data = data,
                Version = 1,
                //Id = Guid.NewGuid()
            };

            var filter = Builders<MongoDbSslData<IProcessManagerData>>.Filter.Eq("Data.CorrelationId", mongoDbData.Data.CorrelationId);
            collection.ReplaceOne(filter, mongoDbData, new ReplaceOptions { IsUpsert = true});
        }

        /// <summary>
        /// Update data of existing ProcessManager. 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="persistanceData"></param>
        public void UpdateData<T>(IPersistanceData<T> persistanceData) where T : class, IProcessManagerData
        {
            var collectionName = GetCollectionName(persistanceData.Data);

            IMongoCollection<MongoDbSslData<T>> collection = _mongoDatabase.GetCollection<MongoDbSslData<T>>(collectionName);
            var indexOptions = new CreateIndexOptions();
            var indexKeys = Builders<MongoDbSslData<T>>.IndexKeys.Ascending("Data.CorrelationId");
            var indexModel = new CreateIndexModel<MongoDbSslData<T>>(indexKeys, indexOptions);
            collection.Indexes.CreateOne(indexModel);

            var versionData = (MongoDbSslData<T>)persistanceData;

            int currentVersion = versionData.Version;
            var filter =
                Builders<MongoDbSslData<T>>.Filter.Eq("Data.CorrelationId", versionData.Data.CorrelationId) &
                Builders<MongoDbSslData<T>>.Filter.Eq(_ => _.Version, currentVersion);
            versionData.Version += 1;
            var result = collection.ReplaceOne(filter, versionData);

            if (result.IsAcknowledged && result.ModifiedCount == 0)
                throw new ArgumentException(string.Format("Possible Concurrency Error. ProcessManagerData with CorrelationId {0} and Version {1} could not be updated.", versionData.Data.CorrelationId, versionData.Version));
        }

        /// <summary>
        /// Removes existing instance of ProcessManager from the database.
        /// </summary>
        /// <param name="persistanceData"></param>
        public void DeleteData<T>(IPersistanceData<T> persistanceData) where T : class, IProcessManagerData
        {
            var collectionName = GetCollectionName(persistanceData.Data);

            IMongoCollection<IPersistanceData<T>> collection = _mongoDatabase.GetCollection<IPersistanceData<T>>(collectionName);
            var indexOptions = new CreateIndexOptions();
            var indexKeys = Builders<IPersistanceData<T>>.IndexKeys.Ascending("Data.CorrelationId");
            var indexModel = new CreateIndexModel<IPersistanceData<T>>(indexKeys, indexOptions);
            collection.Indexes.CreateOne(indexModel);

            var filter = Builders<IPersistanceData<T>>.Filter.Eq("Data.CorrelationId", persistanceData.Data.CorrelationId);
            collection.DeleteOne(filter);
        }

        public void InsertTimeout(TimeoutData timeoutData)
        {
            IMongoCollection<TimeoutData> collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            var indexOptions = new CreateIndexOptions();
            var indexKeys = Builders<TimeoutData>.IndexKeys.Ascending("Id");
            var indexModel = new CreateIndexModel<TimeoutData>(indexKeys, indexOptions);
            collection.Indexes.CreateOne(indexModel);

            collection.InsertOne(timeoutData);

            TimeoutInserted?.Invoke(timeoutData.Time);
        }

        public TimeoutsBatch GetTimeoutsBatch()
        {
            var retval = new TimeoutsBatch { DueTimeouts = new List<TimeoutData>() };

            IMongoCollection<TimeoutData> collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);

            DateTime utcNow = DateTime.UtcNow;

            // Find all the due timeouts and put a lock on each one to prevent multiple threads/processes getting hold of the same data.
            bool doQuery = true;
            while (doQuery)
            {
                var filter1 = Builders<TimeoutData>.Filter.Eq(_ => _.Locked, false) &
                              Builders<TimeoutData>.Filter.Lte(_ => _.Time, utcNow);

                var update = Builders<TimeoutData>.Update.Set(_ => _.Locked, true);
                var result = collection.UpdateOne(filter1, update);

                if (result.ModifiedCount == 0)
                {
                    doQuery = false;
                }
                else
                {
                    var filter2 = Builders<TimeoutData>.Filter.Eq(s => s.Id, result.UpsertedId);
                    retval.DueTimeouts.Add(collection.Find(filter2).First());
                }
            }

            // Get next query time
            var nextQueryTime = DateTime.MaxValue;
            var filter = Builders<TimeoutData>.Filter.Eq(_=>_.Time, utcNow);
            var upcomingTimeoutsRes = collection.Find(filter);
            foreach (TimeoutData upcomingTimeout in upcomingTimeoutsRes.ToList())
            {
                if (upcomingTimeout.Time < nextQueryTime)
                {
                    nextQueryTime = upcomingTimeout.Time;
                }
            }

            if (nextQueryTime == DateTime.MaxValue)
            {
                nextQueryTime = utcNow.AddMinutes(1);
            }

            retval.NextQueryTime = nextQueryTime;

            return retval;
        }

        public void RemoveDispatchedTimeout(Guid id)
        {
            IMongoCollection<TimeoutData> collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);

            var filter = Builders<TimeoutData>.Filter.Eq(_ => _.Locked, true) &
                          Builders<TimeoutData>.Filter.Lte(_ => _.Id, id);

            collection.DeleteOne(filter);
        }

        private static string GetCollectionName<T>(T data) where T : class, IProcessManagerData
        {
            Type typeParameterType = data.GetType();
            var collectionName = typeParameterType.Name;
            return collectionName;
        }
    }
}
