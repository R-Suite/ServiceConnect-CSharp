using System;
using MongoDB.Driver;
using MongoDB.Driver.Builders;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Persistance.MongoDb
{
    /// <summary>
    /// MonoDb implementation of IProcessManagerFinder.
    /// </summary>
    public class MongoDbProcessManagerFinder : IProcessManagerFinder
    {
        private readonly MongoDatabase _mongoDatabase;

        public MongoDbProcessManagerFinder(string connectionString, string databaseName)
        {
            var mongoClient = new MongoClient(connectionString);
            MongoServer server = mongoClient.GetServer();
            _mongoDatabase = server.GetDatabase(databaseName);
        }
        
        /// <summary>
        /// Creates and returns a new instance of MongoDbData 
        /// </summary>
        /// <typeparam name="T">Type of ProcessManager Data</typeparam>
        /// <returns></returns>
        public IPersistanceData<T> NewData<T>() where T : class, IProcessManagerData
        {
            return new MongoDbData<T>
            {
                Version = 1
            };
        }


        /// <summary>
        /// Find existing instance of ProcessManager
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="id"></param>
        /// <returns></returns>
        public IPersistanceData<T> FindData<T>(Guid id) where T : class, IProcessManagerData
        {
            var collectionName = typeof(T).Name;

            MongoCollection<T> collection = _mongoDatabase.GetCollection<T>(collectionName);
            IMongoQuery query = Query<MongoDbData<T>>.Where(i => i.Data.CorrelationId == id);
            return collection.FindOneAs<MongoDbData<T>>(query);
        }

        /// <summary>
        /// Create new instance of ProcessManager
        /// When multiple threads try to create new ProcessManager instance, only the first one is allowed. 
        /// All subsequent threads will update data instead.
        /// </summary>
        /// <param name="persistanceData"></param>
        public void InsertData<T>(IPersistanceData<T> persistanceData) where T : class, IProcessManagerData
        {
            var collectionName = GetCollectionName(persistanceData.Data);

            MongoCollection collection = _mongoDatabase.GetCollection<T>(collectionName);

            collection.FindAndModify(Query.EQ("CorrelationId", persistanceData.Data.CorrelationId), SortBy.Null, Update.Replace(persistanceData), false, true);
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

            collection.Remove(Query.EQ("CorrelationId", persistanceData.Data.CorrelationId));
        }

        private static string GetCollectionName<T>(T data) where T : class, IProcessManagerData
        {
            Type typeParameterType = data.GetType();
            var collectionName = typeParameterType.Name;
            return collectionName;
        }
    }
}
