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
using MongoDB.Driver;
using MongoDB.Driver.Builders;
using ServiceConnect.Interfaces;
using System.Reflection;

namespace ServiceConnect.Persistance.MongoDb
{
    /// <summary>
    /// MonoDb implementation of IProcessManagerFinder.
    /// </summary>
    public class MongoDbProcessManagerFinder : IProcessManagerFinder
    {
        private readonly MongoDatabase _mongoDatabase;
        private const string TimeoutsCollectionName = "Timeouts";

        public event TimeoutInsertedDelegate TimeoutInserted;

        public MongoDbProcessManagerFinder(string connectionString, string databaseName)
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
            var mapping = mapper.Mappings.FirstOrDefault(m => m.MessageType == message.GetType()) ??
                          mapper.Mappings.First(m => m.MessageType == typeof(Message));

            var collectionName = typeof(T).Name;
            MongoCollection<T> collection = _mongoDatabase.GetCollection<T>(collectionName);
            collection.CreateIndex("CorrelationId");

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
            ParameterExpression pe = Expression.Parameter(typeof(MongoDbData<T>), "t");
            Expression left = Expression.Property(pe, typeof(MongoDbData<T>).GetTypeInfo().GetProperty("Data"));
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

            var lambda = Expression.Lambda<Func<MongoDbData<T>, bool>>(expression, pe);
            IMongoQuery query = Query<MongoDbData<T>>.Where(lambda);
            
            return collection.FindOneAs<MongoDbData<T>>(query);
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

            var mongoDbData = new MongoDbData<IProcessManagerData>
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

            var versionData = (MongoDbData<T>)persistanceData;

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

        public void InsertTimeout(TimeoutData timeoutData)
        {
            MongoCollection collection = _mongoDatabase.GetCollection(TimeoutsCollectionName);
            collection.CreateIndex("Id");

            collection.Insert(timeoutData);

            if (TimeoutInserted != null)
            {
                TimeoutInserted(timeoutData.Time);
            }
        }

        public TimeoutsBatch GetTimeoutsBatch()
        {
            var retval = new TimeoutsBatch {DueTimeouts = new List<TimeoutData>()};

            MongoCollection<TimeoutData> collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);

            DateTime utcNow = DateTime.UtcNow;

            // Find all the due timeouts and put a lock on each one to prevent multiple threads/processes getting hold of the same data.
            bool doQuery = true;
            while (doQuery)
            {
                var args = new FindAndModifyArgs
                {
                    Query = Query.And(Query.EQ("Locked", false), Query.LTE("Time", utcNow)),
                    Update = Update<TimeoutData>.Set(c => c.Locked, true),
                    Upsert = false,
                    VersionReturned = FindAndModifyDocumentVersion.Original
                };
                FindAndModifyResult result = collection.FindAndModify(args);

                if (result.ModifiedDocument == null)
                {
                    doQuery = false;
                }
                else
                {
                    retval.DueTimeouts.Add(result.GetModifiedDocumentAs<TimeoutData>());
                }
            }

            // Get next query time
            var nextQueryTime = DateTime.MaxValue;
            var upcomingTimeoutsRes = collection.Find(Query.GT("Time", utcNow));
            foreach (TimeoutData upcomingTimeout in upcomingTimeoutsRes)
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
            MongoCollection<TimeoutData> collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);

            var args = new FindAndRemoveArgs
            {
                Query = Query.And(Query.EQ("Locked", true), Query.EQ("_id", id))
            };

            collection.FindAndRemove(args);
        }

        private static string GetCollectionName<T>(T data) where T : class, IProcessManagerData
        {
            Type typeParameterType = data.GetType();
            var collectionName = typeParameterType.Name;
            return collectionName;
        }
    }
}
