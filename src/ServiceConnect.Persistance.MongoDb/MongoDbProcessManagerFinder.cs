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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
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
        private readonly ConcurrentDictionary<string, bool> _indexedCollections = new ConcurrentDictionary<string, bool>();
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
        /// Create new instance of ProcessManager, or replace the data of the one with the same CorrelationId.
        /// Calls that run at the same time for a new CorrelationId can each insert a document.
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
            var filter = Builders<MongoDbData<T>>.Filter.Eq(_ => _.Id, versionData.Id) &
                         Builders<MongoDbData<T>>.Filter.Eq("Data.CorrelationId", versionData.Data.CorrelationId) &
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

            collection.DeleteMany(Builders<MongoDbData<T>>.Filter.Eq("Data.CorrelationId", persistanceData.Data.CorrelationId));
        }

        public void InsertTimeout(TimeoutData timeoutData)
        {
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);

            collection.InsertOne(timeoutData);

            TimeoutInserted?.Invoke(timeoutData.Time);
        }

        public TimeoutsBatch GetTimeoutsBatch()
        {
            var retval = new TimeoutsBatch { DueTimeouts = new List<TimeoutData>() };

            // Read raw documents so that one the registered Guid serializer cannot read does not stop the others.
            var collection = _mongoDatabase.GetCollection<BsonDocument>(TimeoutsCollectionName);

            DateTime utcNow = DateTime.UtcNow;

            // Find all the due timeouts and put a lock on each one to prevent multiple threads/processes getting hold of the same data.
            var unreadable = new BsonArray();
            var lockUpdate = Builders<BsonDocument>.Update.Set("Locked", true);
            var lockOptions = new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After };

            while (true)
            {
                var dueFilter = Builders<BsonDocument>.Filter.Eq("Locked", false) &
                                Builders<BsonDocument>.Filter.Lte("Time", utcNow) &
                                Builders<BsonDocument>.Filter.Nin("_id", unreadable);
                var document = collection.FindOneAndUpdate(dueFilter, lockUpdate, lockOptions);
                if (document == null)
                {
                    break;
                }

                try
                {
                    retval.DueTimeouts.Add(BsonSerializer.Deserialize<TimeoutData>(document));
                }
                catch (Exception ex) when (ex is FormatException || ex is BsonException)
                {
                    // Usually a Guid stored in another representation. Unlock it so it is not lost.
                    collection.UpdateOne(Builders<BsonDocument>.Filter.Eq("_id", document["_id"]), Builders<BsonDocument>.Update.Set("Locked", false));
                    unreadable.Add(document["_id"]);
                }
            }

            // Get next query time
            var nextTimeout = collection.Find(Builders<BsonDocument>.Filter.Gt("Time", utcNow))
                .Sort(Builders<BsonDocument>.Sort.Ascending("Time"))
                .Project(Builders<BsonDocument>.Projection.Include("Time"))
                .Limit(1)
                .FirstOrDefault();

            retval.NextQueryTime = nextTimeout?["Time"].ToUniversalTime() ?? utcNow.AddMinutes(1);

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
            if (!_indexedCollections.ContainsKey(collectionName))
            {
                collection.Indexes.CreateOne(new CreateIndexModel<MongoDbData<T>>(Builders<MongoDbData<T>>.IndexKeys.Ascending("Data.CorrelationId")));
                _indexedCollections.TryAdd(collectionName, true);
            }

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
