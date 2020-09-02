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
using MongoDB.Driver;
using MongoDB.Driver.Builders;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistance.MongoDb
{
    public class MongoDBAggregatorPersistor : IAggregatorPersistor
    {
        private readonly MongoCollection<MongoDbData<object>> _collection;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="databaseName"></param>
        /// <param name="collectionName"></param>
        public MongoDBAggregatorPersistor(string connectionString, string databaseName, string collectionName)
        {
            var mongoClient = new MongoClient(connectionString);
            MongoServer server = mongoClient.GetServer();
            var mongoDatabase = server.GetDatabase(databaseName);
            _collection = mongoDatabase.GetCollection<MongoDbData<object>>(collectionName);
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

        public void RemoveData(string name, Guid correlationsId)
        {
            _collection.Remove(
                Query.And(
                    Query<MongoDbData<object>>.EQ(x => x.Name, name),
                    Query<MongoDbData<Message>>.EQ(x => x.Data.CorrelationId, correlationsId)
                )
            );
        }
        
        public int Count(string name)
        {
            return Convert.ToInt32(_collection.Count(Query<MongoDbData<object>>.EQ(x => x.Name, name)));
        }
    }
}
