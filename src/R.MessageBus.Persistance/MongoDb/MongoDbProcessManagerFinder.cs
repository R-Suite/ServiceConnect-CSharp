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
        /// <param name="data"></param>
        public void InsertData(IProcessManagerData data) 
        {
            var collectionName = GetCollectionName(data);

            MongoCollection collection = _mongoDatabase.GetCollection(collectionName);

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
