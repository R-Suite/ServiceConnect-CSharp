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

        public VersionData<T> FindData<T>(Guid id) where T : class, IProcessManagerData
        {
            string key = id.ToString();

            return (new VersionData<T> {Data = (T)Cache[key]});
        }

        public void InsertData<T>(VersionData<T> versionData) where T : IProcessManagerData
        {

            string key = versionData.Data.CorrelationId.ToString();

            if (!Cache.Contains(key))
            {
                Cache.Set(key, versionData.Data, _policy);
            }
            else
            {
                throw new ArgumentException(string.Format("ProcessManagerData with CorrelationId {0} already exists in the cache.", key));
            }
        }

        public void UpdateData<T>(VersionData<T> versionData) where T : IProcessManagerData
        {
            string key = versionData.Data.CorrelationId.ToString();

            if (Cache.Contains(key))
            {
                Cache.Set(key, versionData.Data, _policy);
            }
            else
            {
                throw new ArgumentException(string.Format("ProcessManagerData with CorrelationId {0} does not exist in the cache.", key));
            }
        }

        public void DeleteData<T>(VersionData<T> versionData) where T : IProcessManagerData
        {
            string key = versionData.Data.CorrelationId.ToString();

            if (Cache.Contains(key))
            {
                Cache.Remove(key);
            }
        }
    }
}
