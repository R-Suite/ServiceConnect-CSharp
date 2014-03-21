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
        public VersionData<T> FindData<T>(Guid id) where T : class, IProcessManagerData
        {
            var collectionName = typeof(T).Name;

            MongoCollection<T> collection = _mongoDatabase.GetCollection<T>(collectionName);
            IMongoQuery query = Query<VersionData<T>>.Where(i => i.Data.CorrelationId == id);
            return collection.FindOneAs<VersionData<T>>(query);
        }

        /// <summary>
        /// Create new instance of ProcessManager
        /// When multiple threads try to create new ProcessManager instance, only the first one is allowed. 
        /// All subsequent threads will update data instead.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="versionData"></param>
        public void InsertData<T>(VersionData<T> versionData) where T : IProcessManagerData
        {
            var collectionName = GetCollectionName(versionData.Data);

            MongoCollection<T> collection = _mongoDatabase.GetCollection<T>(collectionName);

            collection.FindAndModify(Query.EQ("CorrelationId", versionData.Data.CorrelationId), SortBy.Null, Update.Replace(versionData), false, true);
        }

        /// <summary>
        /// Update data of existing ProcessManager. 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="versionData"></param>
        public void UpdateData<T>(VersionData<T> versionData) where T : IProcessManagerData
        {
            var collectionName = GetCollectionName(versionData.Data);

            MongoCollection<T> collection = _mongoDatabase.GetCollection<T>(collectionName);

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
        /// <typeparam name="T"></typeparam>
        /// <param name="versionData"></param>
        public void DeleteData<T>(VersionData<T> versionData) where T : IProcessManagerData
        {
            var collectionName = GetCollectionName(versionData.Data);

            MongoCollection<T> collection = _mongoDatabase.GetCollection<T>(collectionName);

            collection.Remove(Query.EQ("CorrelationId", versionData.Data.CorrelationId));
        }

        private static string GetCollectionName<T>(T data) where T : IProcessManagerData
        {
            Type typeParameterType = data.GetType();
            var collectionName = typeParameterType.Name;
            return collectionName;
        }
    }
}
