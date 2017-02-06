using System;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistance.MongoDb;

namespace Ruffer.Reporting.SqlTransformation
{
    public class SingletonProcessManagerFinder : IProcessManagerFinder
    {
        private static readonly MongoDbProcessManagerFinder Finder;

        static SingletonProcessManagerFinder()
        {
            Finder = new MongoDbProcessManagerFinder(
                "mongodb://localhost/", 
                "TestPM");
        }

        public SingletonProcessManagerFinder(string connectionString, string databaseName)
        {
        }

        public IPersistanceData<T> FindData<T>(IProcessManagerPropertyMapper mapper, Message message) where T : class, IProcessManagerData
        {
            return Finder.FindData<T>(mapper, message);
        }

        public void InsertData(IProcessManagerData data)
        {
            Finder.InsertData(data);
        }

        public void UpdateData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData
        {
            Finder.UpdateData(data);
        }

        public void DeleteData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData
        {
            Finder.DeleteData(data);
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