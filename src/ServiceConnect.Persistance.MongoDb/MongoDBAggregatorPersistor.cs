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
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistance.MongoDb
{
    public class MongoDBAggregatorPersistor : IAggregatorPersistor
    {
        private readonly IMongoCollection<MongoDbData<object>> _collection;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="databaseName"></param>
        /// <param name="collectionName"></param>
        public MongoDBAggregatorPersistor(string connectionString, string databaseName, string collectionName)
        {
            var mongoDatabase = MongoDbConnection.GetDatabase(connectionString, databaseName);
            _collection = mongoDatabase.GetCollection<MongoDbData<object>>(collectionName);
        }

        public void InsertData(object data, string name)
        {
            _collection.InsertOne(new MongoDbData<object>
            {
                Id = Guid.NewGuid(),
                Name = name,
                Data = data,
                Version = 1
            });
        }

        public IList<object> GetData(string name)
        {
            // Data is stored under its type's assembly-qualified name. Mapping that type first lets the driver
            // resolve the stored discriminator even when nothing has been inserted since the process started.
            var dataType = Type.GetType(name, false);
            if (dataType != null)
            {
                BsonClassMap.LookupClassMap(dataType);
            }

            return _collection.Find(Builders<MongoDbData<object>>.Filter.Eq(x => x.Name, name)).ToList().Select(x => x.Data).ToList();
        }

        public void RemoveData(string name, Guid correlationsId)
        {
            var filter = Builders<MongoDbData<object>>.Filter.Eq(x => x.Name, name) &
                         Builders<MongoDbData<object>>.Filter.Eq("Data.CorrelationId", correlationsId);

            _collection.DeleteMany(filter);
        }
        
        public int Count(string name)
        {
            return Convert.ToInt32(_collection.CountDocuments(Builders<MongoDbData<object>>.Filter.Eq(x => x.Name, name)));
        }
    }
}
