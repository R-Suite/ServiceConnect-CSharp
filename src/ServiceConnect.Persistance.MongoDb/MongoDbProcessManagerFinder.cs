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
using ServiceConnect.Interfaces;
using System.Reflection;

namespace ServiceConnect.Persistance.MongoDb
{
    /// <summary>
    /// MonoDb implementation of IProcessManagerFinder.
    /// </summary>
    public class MongoDbProcessManagerFinder : IProcessManagerFinder
    {
        private readonly IMongoDatabase _mongoDatabase;
        private const string TimeoutsCollectionName = "Timeouts";

        public event TimeoutInsertedDelegate TimeoutInserted;

        public MongoDbProcessManagerFinder(string connectionString, string databaseName)
        {
            _mongoDatabase = MongoDbConnection.GetDatabase(connectionString, databaseName);
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

            var collection = GetCollection<T>(typeof(T).Name);

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
            var collection = GetCollection<IProcessManagerData>(GetCollectionName(data));

            var filter = Builders<MongoDbData<IProcessManagerData>>.Filter.Eq("Data.CorrelationId", data.CorrelationId);
            var update = Builders<MongoDbData<IProcessManagerData>>.Update
                .Set(_ => _.Data, data)
                .Set(_ => _.Version, 1)
                .SetOnInsert(_ => _.Id, Guid.NewGuid());

            collection.UpdateOne(filter, update, new UpdateOptions { IsUpsert = true });
        }

        /// <summary>
        /// Update data of existing ProcessManager. 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="persistanceData"></param>
        public void UpdateData<T>(IPersistanceData<T> persistanceData) where T : class, IProcessManagerData
        {
            var collection = GetCollection<T>(GetCollectionName(persistanceData.Data));

            var versionData = (MongoDbData<T>)persistanceData;

            int currentVersion = versionData.Version;
            var filter = Builders<MongoDbData<T>>.Filter.Eq("Data.CorrelationId", versionData.Data.CorrelationId) &
                         Builders<MongoDbData<T>>.Filter.Eq(_ => _.Version, currentVersion);
            versionData.Version += 1;
            var result = collection.ReplaceOne(filter, versionData);

            if (result.IsAcknowledged && result.MatchedCount == 0)
                throw new ArgumentException(string.Format("Possible Concurrency Error. ProcessManagerData with CorrelationId {0} and Version {1} could not be updated.", versionData.Data.CorrelationId, versionData.Version));
        }

        /// <summary>
        /// Removes existing instance of ProcessManager from the database.
        /// </summary>
        /// <param name="persistanceData"></param>
        public void DeleteData<T>(IPersistanceData<T> persistanceData) where T : class, IProcessManagerData
        {
            var collection = GetCollection<T>(GetCollectionName(persistanceData.Data));

            collection.DeleteOne(Builders<MongoDbData<T>>.Filter.Eq("Data.CorrelationId", persistanceData.Data.CorrelationId));
        }

        public void InsertTimeout(TimeoutData timeoutData)
        {
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);

            collection.InsertOne(timeoutData);

            TimeoutInserted?.Invoke(timeoutData.Time);
        }

        public TimeoutsBatch GetTimeoutsBatch()
        {
            var retval = new TimeoutsBatch {DueTimeouts = new List<TimeoutData>()};

            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);

            DateTime utcNow = DateTime.UtcNow;

            // Find all the due timeouts and put a lock on each one to prevent multiple threads/processes getting hold of the same data.
            var dueFilter = Builders<TimeoutData>.Filter.Eq(_ => _.Locked, false) &
                            Builders<TimeoutData>.Filter.Lte(_ => _.Time, utcNow);
            var lockUpdate = Builders<TimeoutData>.Update.Set(_ => _.Locked, true);
            var lockOptions = new FindOneAndUpdateOptions<TimeoutData> { ReturnDocument = ReturnDocument.After };

            TimeoutData dueTimeout;
            while ((dueTimeout = collection.FindOneAndUpdate(dueFilter, lockUpdate, lockOptions)) != null)
            {
                retval.DueTimeouts.Add(dueTimeout);
            }

            // Get next query time
            var nextQueryTime = DateTime.MaxValue;
            var upcomingTimeouts = collection.Find(Builders<TimeoutData>.Filter.Gt(_ => _.Time, utcNow)).ToList();
            foreach (TimeoutData upcomingTimeout in upcomingTimeouts)
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
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);

            collection.DeleteOne(Builders<TimeoutData>.Filter.Eq(_ => _.Locked, true) &
                                 Builders<TimeoutData>.Filter.Eq(_ => _.Id, id));
        }

        private IMongoCollection<MongoDbData<T>> GetCollection<T>(string collectionName)
        {
            var collection = _mongoDatabase.GetCollection<MongoDbData<T>>(collectionName);
            collection.Indexes.CreateOne(new CreateIndexModel<MongoDbData<T>>(Builders<MongoDbData<T>>.IndexKeys.Ascending("Data.CorrelationId")));
            return collection;
        }

        private static string GetCollectionName<T>(T data) where T : class, IProcessManagerData
        {
            Type typeParameterType = data.GetType();
            var collectionName = typeParameterType.Name;
            return collectionName;
        }
    }
}
