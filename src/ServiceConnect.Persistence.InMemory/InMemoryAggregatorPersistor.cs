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

namespace ServiceConnect.Persistence.InMemory
{
    public class InMemoryAggregatorPersistor : IAggregatorPersistor
    {
        private readonly object _memoryCacheLock = new object();

        private readonly ICacheProvider _provider = new CacheProvider();
        private readonly DateTime _absoluteExpiry = DateTime.Now.AddDays(2);

        /// <summary>
        /// Constructor (parameters not used but needed)
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="databaseName"></param>
        /// <param name="collectionName"></param>
        public InMemoryAggregatorPersistor(string connectionString, string databaseName, string collectionName)
        { }

        public void InsertData(object data, string name)
        {
            lock (_memoryCacheLock)
            {
                if (_provider.Contains(name))
                {
                    var cacheItem = _provider.Get<string, object>(name);
                    ((IList<object>)cacheItem).Add(data);
                }
                else
                {
                    _provider.Add(name, new List<object> { data }, _absoluteExpiry);
                }
            }
        }

        public IList<object> GetData(string name)
        {
            lock (_memoryCacheLock)
            {
                if (_provider.Contains(name))
                {
                    var cacheItem = _provider.Get<string, object>(name);
                    return ((List<object>)cacheItem).ToList();
                }
                return new List<object>();
            }
        }

        public void RemoveData(string name, Guid correlationsId)
        {
            lock (_memoryCacheLock)
            {
                if (_provider.Contains(name))
                {
                    var cacheItem = (List<object>)_provider.Get<string, object>(name);
                    var message = cacheItem.FirstOrDefault(x => x is Message m && m.CorrelationId == correlationsId);
                    if (message != null)
                        cacheItem.Remove(message);
                }
            }
        }

        public void RemoveAll(string name)
        {
            lock (_memoryCacheLock)
            {
                if (_provider.Contains(name))
                {
                    _provider.Remove(name);
                }
            }
        }

        public int Count(string name)
        {
            lock (_memoryCacheLock)
            {
                if (_provider.Contains(name))
                {
                    var cacheItem = (List<object>)_provider.Get<string, object>(name);
                    return cacheItem.Count;
                }
                return 0;
            }
        }
    }
}
