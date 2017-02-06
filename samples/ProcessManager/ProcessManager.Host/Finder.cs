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
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Builders;
using ProcessManager.Messages;
using ServiceConnect.Interfaces;

namespace Ruffer.Reporting.SqlTransformation
{
    /// <summary>
    /// MonoDb implementation of IProcessManagerFinder.
    /// </summary>
    public class Finder : IProcessManagerFinder
    {
        private readonly MongoDatabase _mongoDatabase;
        private const string TimeoutsCollectionName = "Timeouts";

        public Finder(string connectionString, string databaseName)
        {
            var mongoClient = new MongoClient(connectionString);
            MongoServer server = mongoClient.GetServer();
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
            if (message is StartProcessManagerMessage)
            {
                return null;
            }

            var mapping = mapper.Mappings.FirstOrDefault(m => m.MessageType == message.GetType()) ??
                          mapper.Mappings.First(m => m.MessageType == typeof(Message));

            var collectionName = typeof(T).Name;
            MongoCollection<T> collection = _mongoDatabase.GetCollection<T>(collectionName);

            object msgPropValue = mapping.MessageProp.Invoke(message);
            if (null == msgPropValue)
            {
                throw new ArgumentException("Message property expression evaluates to null");
            }

            //Left
            ParameterExpression pe = Expression.Parameter(typeof(MyMongoData<T>), "t");
            Expression left = Expression.Property(pe, typeof(MyMongoData<T>).GetTypeInfo().GetProperty("Data"));
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

            var lambda = Expression.Lambda<Func<MyMongoData<T>, bool>>(expression, pe);
            IMongoQuery query = Query<MyMongoData<T>>.Where(lambda);


            // check if data is locked
            FindAndModifyResult result = collection.FindAndModify(new FindAndModifyArgs
            {
                Query = Query.And(
                    Query.Or(
                        Query.EQ("Locked", false),
                        Query.LTE("LockTimeout", DateTime.UtcNow)
                    ),
                    query
                ),
                Update = Update.Combine(
                    Update.Set("Locked", true),
                    Update.Set("LockTimeout", DateTime.UtcNow.AddSeconds(30))
                )
            });

            if (result.ModifiedDocument == null)
            {
                // spin until lock is released
                while (true)
                {
                    result = collection.FindAndModify(new FindAndModifyArgs
                    {
                        Query = Query.And(
                            Query.Or(
                                Query.EQ("Locked", false),
                                Query.LTE("LockTimeout", DateTime.UtcNow)
                            ),
                            query
                        ),
                        Update = Update.Combine(
                            Update.Set("Locked", true),
                            Update.Set("LockTimeout", DateTime.UtcNow.AddSeconds(30))
                        )
                    });

                    // Found unlocked data
                    if (result.ModifiedDocument != null)
                    {
                        break;
                    }

                    Thread.Sleep(100);
                }
            }

            return result.GetModifiedDocumentAs<MyMongoData<T>>();
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

            var mongoDbData = new MyMongoData<IProcessManagerData>
            {
                Data = data,
                Version = 1,
                Id = Guid.NewGuid(),
                Locked = false
            };

            collection.FindAndModify(Query.EQ("CorrelationId", mongoDbData.Data.CorrelationId), SortBy.Null, Update.Replace(mongoDbData), false, true);
        }

        private class MyMongoData<T> : IPersistanceData<T>
        {
            public Guid Id { get; set; }
            public int Version { get; set; }
            public T Data { get; set; }
            public string Name { get; set; }
            public bool Locked { get; set; }
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

            var versionData = (MyMongoData<T>)persistanceData;
            versionData.Locked = false;

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

            collection.Remove(Query.EQ("Data.CorrelationId", persistanceData.Data.CorrelationId));
        }

        private static string GetCollectionName<T>(T data) where T : class, IProcessManagerData
        {
            Type typeParameterType = data.GetType();
            var collectionName = typeParameterType.Name;
            return collectionName;
        }

        public void InsertTimeout(TimeoutData timeoutData)
        {
            throw new NotImplementedException();
        }

        public TimeoutsBatch GetTimeoutsBatch()
        {
            throw new NotImplementedException();
        }

        public void RemoveDispatchedTimeout(Guid id)
        {
            throw new NotImplementedException();
        }

        public event TimeoutInsertedDelegate TimeoutInserted;
    }
}
