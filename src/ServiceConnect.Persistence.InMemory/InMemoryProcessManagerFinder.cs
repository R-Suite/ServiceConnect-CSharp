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
using System.Linq.Expressions;
using System.Reflection;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistence.InMemory
{
    /// <summary>
    /// InMemory implementation of IProcessManagerFinder for testing and rapid development
    /// </summary>
    public class InMemoryProcessManagerFinder : IProcessManagerFinder
    {
        private readonly object _memoryCacheLock = new object();

        private ICacheProvider _provider = new CacheProvider();
        private DateTime _absoluteExpiry = DateTime.Now.AddDays(2);

        /// <summary>
        /// Constructor (parameters not used but needed)
        /// </summary>
        /// <param name="connectionString"></param>
        /// <param name="databaseName"></param>
        public InMemoryProcessManagerFinder(string connectionString, string databaseName)
        { }

        public event TimeoutInsertedDelegate TimeoutInserted;

        public IPersistenceData<T> FindData<T>(IProcessManagerPropertyMapper mapper, Message message) where T : class, IProcessManagerData
        {
            lock (_memoryCacheLock)
            {
                var mapping = mapper.Mappings.FirstOrDefault(m => m.MessageType == message.GetType()) ??
                          mapper.Mappings.First(m => m.MessageType == typeof(Message));

                object? msgPropValue = null;

                try
                {
                    msgPropValue = mapping.MessageProp.Invoke(message);
                }
                catch
                {
                    return null;
                }

                if (null == msgPropValue)
                {
                    throw new ArgumentException("Message property expression evaluates to null");
                }

                //Left
                ParameterExpression pe = Expression.Parameter(typeof(MemoryData<T>), "t");
                Expression left = Expression.Property(pe, typeof(MemoryData<T>).GetTypeInfo().GetProperty("Data")!);
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

                var newCacheItems = new List<MemoryData<T>>();

                foreach (var key in _provider.Keys())
                {
                    var value = _provider.Get<string, object>(key.ToString()!);
                    if (value.GetType() == typeof(MemoryData<T>))
                    {
                        var typed = (MemoryData<T>)value;
                        newCacheItems.Add(new MemoryData<T> { Data = typed.Data, Version = typed.Version });
                    }
                    else
                    {
                        // Support case where data was stored with a different generic parameter
                        // (e.g., stored as MemoryData<ConcreteType>, queried as MemoryData<IProcessManagerData>)
                        dynamic dyn = value;
                        try
                        {
                            object data = dyn.Data;
                            if (data is T typedData)
                            {
                                newCacheItems.Add(new MemoryData<T> { Data = typedData, Version = (int)dyn.Version });
                            }
                        }
                        catch { /* not a MemoryData-like object, skip */ }
                    }
                }

                MemoryData<T>? retval = newCacheItems.FirstOrDefault(lambda.Compile());

                return retval;
            }
        }

        public void InsertData(IProcessManagerData data)
        {
            Type typeParameterType = data.GetType();

            MethodInfo md = GetType().GetTypeInfo().GetMethods().First(m => m.Name == "GetMemoryData" && m.GetParameters()[0].Name == "data");
            MethodInfo genericMd = md.MakeGenericMethod(typeParameterType);

            lock (_memoryCacheLock)
            {
                var memoryData = genericMd.Invoke(this, new object[] {data});

                string key = data.CorrelationId.ToString();

                if (!_provider.Contains(key))
                {
                    _provider.Add(key, memoryData!, _absoluteExpiry);
                }
                else
                {
                    throw new ArgumentException(string.Format("ProcessManagerData with CorrelationId {0} already exists in the cache.", key));
                }
            }
        }

        public MemoryData<DT> GetMemoryData<DT>(DT data) where DT : class, IProcessManagerData
        {
            var memoryData = new MemoryData<DT>
            {
                Data = data,
                Version = 1,
                Id = Guid.NewGuid()
            };

            return memoryData;
        }

        public void UpdateData<T>(IPersistenceData<T> data) where T : class, IProcessManagerData
        {
            lock (_memoryCacheLock)
            {
                string? error = null;
                var newData = (MemoryData<T>) data;
                string key = data.Data.CorrelationId.ToString();

                if (_provider.Contains(key))
                {
                    // Use dynamic to read the version, since the stored MemoryData<X>
                    // generic parameter may differ from T (e.g., concrete vs interface).
                    dynamic storedData = _provider.Get<string, object>(key);
                    int currentVersion = (int)storedData.Version;

                    var updatedData = new MemoryData<T>
                    {
                        Data = data.Data,
                        Version = newData.Version + 1
                    };

                    if (currentVersion == newData.Version)
                    {
                        _provider.Remove(key);
                        _provider.Add(key, updatedData, _absoluteExpiry);
                    }
                    else
                    {
                        error = string.Format("Possible Concurrency Error. ProcessManagerData with CorrelationId {0} and Version {1} could not be updated.", key, currentVersion);
                    }
                }
                else
                {
                    error = string.Format("ProcessManagerData with CorrelationId {0} does not exist in memory.", key);
                }

                if (!string.IsNullOrEmpty(error))
                {
                    throw new ArgumentException(error);
                }
            }
        }

        public void DeleteData<T>(IPersistenceData<T> data) where T : class, IProcessManagerData
        {
            lock (_memoryCacheLock)
            {
                string key = data.Data.CorrelationId.ToString();
                _provider.Remove(key);
            }
        }

        public void InsertTimeout(TimeoutData timeoutData)
        {
            lock (_memoryCacheLock)
            {
                string key = timeoutData.Id.ToString();

                if (!_provider.Contains(key))
                {
                    _provider.Add(key, timeoutData, _absoluteExpiry);
                }
                else
                {
                    throw new ArgumentException(string.Format("TimeoutData with Id {0} already exists in the cache.", key));
                }
            }

            TimeoutInserted?.Invoke(timeoutData.Time);
        }

        public TimeoutsBatch GetTimeoutsBatch()
        {
            var retval = new TimeoutsBatch { DueTimeouts = new List<TimeoutData>() };

            DateTime utcNow = DateTime.UtcNow;

            var nextQueryTime = DateTime.MaxValue;

            lock (_memoryCacheLock)
            {
                IDictionary<string, object> cacheItems = new Dictionary<string, object>();

                foreach (var key in _provider.Keys())
                {
                    var value = _provider.Get<string, object>(key.ToString()!);
                    if (value.GetType() == typeof(TimeoutData))
                    {
                        cacheItems.Add(key.ToString()!, value);
                    }
                }

                foreach (var data in cacheItems)
                {
                    var timeoutData = (TimeoutData)data.Value;
                    if (timeoutData.Time <= utcNow)
                    {
                        retval.DueTimeouts.Add(timeoutData);
                    }

                    if (timeoutData.Time > utcNow && timeoutData.Time < nextQueryTime)
                    {
                        nextQueryTime = timeoutData.Time;
                    }
                }
            }

            if (nextQueryTime == DateTime.MaxValue)
            {
                nextQueryTime = utcNow.AddMinutes(1);
            }

            retval.NextQueryTime = nextQueryTime;

            return retval;
        }

        public void RemoveDispatchedTimeout(Guid id)
        {
            _provider.Remove(id.ToString());
        }
    }
}
