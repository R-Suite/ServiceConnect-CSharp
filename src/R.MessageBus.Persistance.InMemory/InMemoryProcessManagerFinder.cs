using System;
using System.Runtime.Caching;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Persistance.InMemory
{
    /// <summary>
    /// InMemory implementation of IProcessManagerFinder for testing and rapid development 
    /// </summary>
    public class InMemoryProcessManagerFinder : IProcessManagerFinder
    {
        private static readonly ObjectCache Cache = MemoryCache.Default;
        readonly CacheItemPolicy _policy = new CacheItemPolicy { Priority = CacheItemPriority.Default };
        private readonly object _memoryCacheLock = new object();

        /// <summary>
        /// Constructor (parameters not used but needed)
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="databaseName"></param>
        public InMemoryProcessManagerFinder(string connectionString, string databaseName)
        { }

        public IPersistanceData<T> FindData<T>(Guid id) where T : class, IProcessManagerData
        {
            string key = id.ToString();

            var memoryData = (MemoryData<IProcessManagerData>)(Cache.Get(key));

            var retval = new MemoryData<T> { Data = null, Version = 0 };

            if (null != memoryData)
            {
                retval = new MemoryData<T> { Data = (T)memoryData.Data, Version = memoryData.Version };
            }

            return retval;
        }

        public void InsertData(IProcessManagerData data)
        {
            var memoryData = new MemoryData<IProcessManagerData>
            {
                Data = data,
                Version = 1
            };

            string key = data.CorrelationId.ToString();

            lock (_memoryCacheLock)
            {
                if (!Cache.Contains(key))
                {
                    Cache.Set(key, memoryData, _policy);
                }
                else
                {
                    throw new ArgumentException(string.Format("ProcessManagerData with CorrelationId {0} already exists in the cache.", key));
                }
            }
        }

        public void UpdateData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData
        {
            string error = null;
            var newData = (MemoryData<T>)data;
            string key = data.Data.CorrelationId.ToString();

            if (Cache.Contains(key))
            {
                var currentVersion = ((MemoryData<IProcessManagerData>)(Cache.Get(key))).Version;

                var updatedData = new MemoryData<IProcessManagerData>
                {
                    Data = data.Data,
                    Version = newData.Version + 1
                };

                lock (_memoryCacheLock)
                {
                    if (currentVersion == newData.Version)
                    {
                        Cache.Set(key, updatedData, _policy);
                    }
                    else
                    {
                        error = string.Format("Possible Concurrency Error. ProcessManagerData with CorrelationId {0} and Version {1} could not be updated.", key, currentVersion);
                    }
                }
            }
            else
            {
                error = string.Format("ProcessManagerData with CorrelationId {0} does not exist in memory.", key);
            }

            if (!string.IsNullOrEmpty(error))
                throw new ArgumentException(error);
        }

        public void DeleteData<T>(IPersistanceData<T> persistanceData) where T : class, IProcessManagerData
        {
            string key = persistanceData.Data.CorrelationId.ToString();

            Cache.Remove(key);
        }
    }
}
