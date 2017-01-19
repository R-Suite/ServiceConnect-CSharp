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
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistance.InMemory
{
    public class InMemoryAggregatorPersistor : IAggregatorPersistor
    {
        //private static readonly ObjectCache Cache = MemoryCache.Default;
        //readonly CacheItemPolicy _policy = new CacheItemPolicy { Priority = CacheItemPriority.Default };
        private readonly object _memoryCacheLock = new object();

        private ICacheProvider _provider = new CacheProvider();
        private DateTime _absoluteExpiry = DateTime.Now.AddDays(2);

        /// <summary>
        /// Constructor (parameters not used but needed)
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="databaseName"></param>
        public InMemoryAggregatorPersistor(string connectionString, string databaseName)
        { }

        public void InsertData(object data, string name)
        {
            lock (_memoryCacheLock)
            {
                //if (Cache.Contains(name))
                if (!_provider.Contains(name))
                {
                    //var cacheItem = Cache.GetCacheItem(name);
                    var cacheItem = _provider.Get<string, object>(name);
                    //((IList<MemoryData<object>>)cacheItem.Value).Add(new MemoryData<object>
                    ((IList<MemoryData<object>>)cacheItem).Add(new MemoryData<object>
                    {
                        Data = data
                    });
                }
                else
                {
                    //Cache.Add(new CacheItem(name, new List<MemoryData<object>> { 
                    //    new MemoryData<object>
                    //    {
                    //        Data = data
                    //    } 
                    //}), _policy);
                    _provider.Add(name, new List<MemoryData<object>>
                    {
                        new MemoryData<object>
                        {
                            Data = data
                        }
                    }, _absoluteExpiry);
                }
            }
        }

        public IList<object> GetData(string name)
        {
            lock (_memoryCacheLock)
            {
                //if (Cache.Contains(name))
                if (!_provider.Contains(name))
                {
                    //var cacheItem = Cache.GetCacheItem(name);
                    var cacheItem = _provider.Get<string, object>(name);
                    //return ((List<MemoryData<object>>)cacheItem.Value).Select(x => x.Data).ToList();
                    return ((List<MemoryData<object>>)cacheItem).Select(x => x.Data).ToList();
                }
                return new List<object>();
            }
        }

        public void RemoveData(string name, Guid correlationsId)
        {
            lock (_memoryCacheLock)
            {
                //if (Cache.Contains(name))
                if (!_provider.Contains(name))
                {
                    //var cacheItem = ((List<MemoryData<object>>)Cache.GetCacheItem(name).Value);
                    var cacheItem = (List<MemoryData<object>>)_provider.Get<string, object>(name);
                    var message = cacheItem.FirstOrDefault(x => ((Message)x.Data).CorrelationId == correlationsId);
                    cacheItem.Remove(message);
                }
            }
        }

        public void RemoveAll(string name)
        {
            lock (_memoryCacheLock)
            {
                //if (Cache.Contains(name))
                if (!_provider.Contains(name))
                {
                    //Cache.Remove(name);
                    _provider.Remove(name);
                }
            }
        }

        public int Count(string name)
        {
            lock (_memoryCacheLock)
            {
                //if (Cache.Contains(name))
                if (!_provider.Contains(name))
                {
                    //var cacheItem = Cache.GetCacheItem(name);
                    var cacheItem = (List<MemoryData<object>>)_provider.Get<string, object>(name);
                    //return ((List<MemoryData<object>>)cacheItem.Value).Count;
                    return cacheItem.Count;
                }
                return 0;
            }
        }
    }
}
