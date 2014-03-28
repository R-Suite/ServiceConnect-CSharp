using System;
using System.Runtime.Caching;
using R.MessageBus.Interfaces;
using R.MessageBus.Persistance.MongoDb;

namespace R.MessageBus.Persistance.InMemory
{
    /// <summary>
    /// InMemory implementation of IProcessManagerFinder for testing and rapid development 
    /// </summary>
    public class InMemoryProcessManagerFinder : IProcessManagerFinder
    {
        private static readonly ObjectCache Cache = MemoryCache.Default;
        readonly CacheItemPolicy _policy = new CacheItemPolicy { Priority = CacheItemPriority.Default };

        public InMemoryProcessManagerFinder(string connectionString, string database)
        {
            
        }

        public IPersistanceData<T> FindData<T>(Guid id) where T : class, IProcessManagerData
        {
            string key = id.ToString();

            return (new MongoDbData<T> {Data = (T)Cache[key]});
        }

        public void InsertData(IProcessManagerData data)
        {
            string key = data.CorrelationId.ToString();

            if (!Cache.Contains(key))
            {
                Cache.Set(key, data, _policy);
            }
            else
            {
                throw new ArgumentException(string.Format("ProcessManagerData with CorrelationId {0} already exists in the cache.", key));
            }
        }

        public void UpdateData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData
        {
            string key = data.Data.CorrelationId.ToString();

            if (Cache.Contains(key))
            {
                Cache.Set(key, data.Data, _policy);
            }
            else
            {
                throw new ArgumentException(string.Format("ProcessManagerData with CorrelationId {0} does not exist in the cache.", key));
            }
        }

        public void DeleteData<T>(IPersistanceData<T> persistanceData) where T : class, IProcessManagerData
        {
            string key = persistanceData.Data.CorrelationId.ToString();

            if (Cache.Contains(key))
            {
                Cache.Remove(key);
            }
        }
    }
}
