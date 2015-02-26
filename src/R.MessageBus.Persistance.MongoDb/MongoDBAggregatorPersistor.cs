using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Driver;
using MongoDB.Driver.Builders;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Persistance.MongoDb
{
    public class MongoDBAggregatorPersistor : IAggregatorPersistor
    {
        private readonly MongoCollection<MongoDbData<object>> _collection;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="databaseName"></param>
        public MongoDBAggregatorPersistor(string connectionString, string databaseName)
        {
            var mongoClient = new MongoClient(connectionString);
            MongoServer server = mongoClient.GetServer();
            var mongoDatabase = server.GetDatabase(databaseName);
            _collection = mongoDatabase.GetCollection<MongoDbData<object>>("Aggregator");
        }

        public void InsertData(object data, string name)
        {
            _collection.Insert(new MongoDbData<object>
            {
                Name = name,
                Data = data,
                Version = 1
            });
        }

        public IList<object> GetData(string name)
        {
            return _collection.Find(Query<MongoDbData<object>>.EQ(x => x.Name, name)).Select(x => x.Data).ToList();
        }

        public void RemoveAll(string name)
        {
            _collection.Remove(Query<MongoDbData<object>>.EQ(x => x.Name, name));
        }

        public int Count(string name)
        {
            return Convert.ToInt32(_collection.Count(Query<MongoDbData<object>>.EQ(x => x.Name, name)));
        }
    }
}
