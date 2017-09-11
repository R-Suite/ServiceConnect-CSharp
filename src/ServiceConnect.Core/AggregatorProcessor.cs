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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Common.Logging;
using Newtonsoft.Json;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Core
{
    /// <summary>
    /// Aggregate messages into batches of a predefined size
    /// and pass them to relevant handlers
    /// </summary>
    public class AggregatorProcessor : IAggregatorProcessor
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(AggregatorProcessor));

        private readonly IAggregatorPersistor _aggregatorPersistor;
        private readonly IBusContainer _container;
        private readonly Type _handlerType;
        private Timer _timer;
        private Type _type;
        private Type _genericListType;
        private readonly object _lock = new object();
        private TimeSpan _timeout;

        public AggregatorProcessor(IAggregatorPersistor aggregatorPersistor, IBusContainer container, Type handlerType)
        {
            _aggregatorPersistor = aggregatorPersistor;
            _container = container;
            _handlerType = handlerType;
        }

        /// <summary>
        /// Start new instance of <see cref="System.Threading.Timer"/> specifying a callback that
        /// get all messages from an aggregator persistance store and 
        /// executes relevant handler type
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="timeout"></param>
        public void StartTimer<T>(TimeSpan timeout)
        {
            _type = typeof(T);
            _timeout = timeout;
            _genericListType = typeof(List<>).MakeGenericType(_type);
            _timer = new Timer(Callback, timeout, timeout, timeout);
        }

        /// <summary>
        /// Reset timer with previously defined <see cref="_timeout"/>
        /// </summary>
        public void ResetTimer()
        {
            if (_timer != null)
            {
                _timer.Change(_timeout, _timeout);
            }
        }

        public void ProcessMessage<T>(string message) where T : Message
        {
            object aggregator = _container.GetInstance(_handlerType);

            var timeout = (TimeSpan)(_handlerType.GetMethod("Timeout").Invoke(aggregator, new object[] { }));
            var batchSize = (int)(_handlerType.GetMethod("BatchSize").Invoke(aggregator, new object[] { }));

            var messageObject = JsonConvert.DeserializeObject(message, typeof(T));

            if (batchSize == 0 && timeout == default(TimeSpan))
            {
                batchSize = 10;
            }
            var typeName = typeof(T).AssemblyQualifiedName;

            lock (_lock)
            {
                _aggregatorPersistor.InsertData(messageObject, typeName);

                if (batchSize != 0)
                {
                    if (_aggregatorPersistor.Count(typeName) >= batchSize)
                    {
                        IList<object> messages = _aggregatorPersistor.GetData(typeName);

                        try
                        {
                            _handlerType.GetMethod("Execute", new[] { typeof(IList<T>) }).Invoke(aggregator, new object[] {messages.Cast<T>().ToList()});
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("Error executing aggregator execute method", ex);
                            throw;
                        }

                        foreach (var persistedMessage in messages)
                        {
                            _aggregatorPersistor.RemoveData(typeName, ((Message)persistedMessage).CorrelationId);
                        }

                        ResetTimer();
                    }
                }
            }
        }

        private void Callback(object state)
        {
            lock (_lock)
            {
                if (_aggregatorPersistor.Count(_type.AssemblyQualifiedName) > 0)
                {
                    object aggregator = _container.GetInstance(_handlerType);
                    var messages = _aggregatorPersistor.GetData(_type.AssemblyQualifiedName);
                    var messageList = (IList)Activator.CreateInstance(_genericListType);

                    foreach (var item in messages)
                    {
                        messageList.Add(item);
                    }

                    try
                    {
                        _handlerType.GetMethod("Execute", new[] { _genericListType }).Invoke(aggregator, new object[] { messageList });
                    }
                    catch (Exception)
                    {
                        Logger.Error("Error executing aggregator execute method");
                        throw;
                    }
                    foreach (var persistedMessage in messages)
                    {
                        _aggregatorPersistor.RemoveData(_type.AssemblyQualifiedName, ((Message)persistedMessage).CorrelationId);
                    }
                }
            }
        }

        /// <summary>
        /// Dispose timer
        /// </summary>
        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}