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
using System.Linq;
using System.Linq.Expressions;
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

        public IPersistanceData<T> FindData<T>(IProcessManagerPropertyMapper mapper, Message message) where T : class, IProcessManagerData
        {
            var mapping = mapper.Mappings.FirstOrDefault(m => m.MessageType == message.GetType()) ??
                          mapper.Mappings.First(m => m.MessageType == typeof (Message));


            object msgPropValue = mapping.MessageProp.Invoke(message);
            if (null == msgPropValue)
            {
                throw new ArgumentException("Message property expression evaluates to null");
            }

            //Left
            ParameterExpression pe = Expression.Parameter(typeof(MemoryData<T>), "t");
            Expression left = Expression.Property(pe, typeof(MemoryData<T>).GetProperty("Data"));
            foreach (var prop in mapping.PropertiesHierarchy.Reverse())
            {
                left = Expression.Property(left, left.Type, prop.Key);
            }

            //Right
            Expression right = Expression.Constant(msgPropValue, msgPropValue.GetType());

            Expression expression;

            try
            {
                expression = Expression.Equal(left, right);
            }
            catch (InvalidOperationException ex)
            {
                throw new Exception("Mapped incompatible types of ProcessManager Data and Message properties.", ex);
            }

            Expression<Func<MemoryData<T>, bool>> lambda = Expression.Lambda<Func<MemoryData<T>, bool>>(expression, pe);

            var cacheItems = (from n in Cache.AsParallel() select n.Value);
            var newCacheItems = Enumerable.ToList((from dynamic cacheItem in cacheItems select new MemoryData<T> { Data = cacheItem.Data, Version = cacheItem.Version}));
            MemoryData<T> retval = newCacheItems.FirstOrDefault(lambda.Compile());

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
