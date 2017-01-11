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
using System.Runtime.Caching;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistance.InMemory
{
    public class InMemoryAggregatorPersistor : IAggregatorPersistor
    {
        private static readonly ObjectCache Cache = MemoryCache.Default;
        readonly CacheItemPolicy _policy = new CacheItemPolicy { Priority = CacheItemPriority.Default };
        private readonly object _memoryCacheLock = new object();

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
                if (Cache.Contains(name))
                {
                    var cacheItem = Cache.GetCacheItem(name);
                    ((IList<MemoryData<object>>)cacheItem.Value).Add(new MemoryData<object>
                    {
                        Data = data
                    });
                }
                else
                {
                    Cache.Add(new CacheItem(name, new List<MemoryData<object>> { 
                        new MemoryData<object>
                        {
                            Data = data
                        } 
                    }), _policy);
                }
            }
        }

        public IList<object> GetData(string name)
        {
            lock (_memoryCacheLock)
            {
                if (Cache.Contains(name))
                {
                    var cacheItem = Cache.GetCacheItem(name);
                    return ((List<MemoryData<object>>)cacheItem.Value).Select(x => x.Data).ToList();
                }
                return new List<object>();
            }
        }

        public void RemoveData(string name, Guid correlationsId)
        {
            lock (_memoryCacheLock)
            {
                if (Cache.Contains(name))
                {
                    var cacheItem = ((List<MemoryData<object>>)Cache.GetCacheItem(name).Value);
                    var message = cacheItem.FirstOrDefault(x => ((Message)x.Data).CorrelationId == correlationsId);
                    cacheItem.Remove(message);
                }
            }
        }

        public void RemoveAll(string name)
        {
            lock (_memoryCacheLock)
            {
                if (Cache.Contains(name))
                {
                    Cache.Remove(name);
                }
            }
        }

        public int Count(string name)
        {
            lock (_memoryCacheLock)
            {
                if (Cache.Contains(name))
                {
                    var cacheItem = Cache.GetCacheItem(name);
                    return ((List<MemoryData<object>>)cacheItem.Value).Count;
                }
                return 0;
            }
        }
    }
}
