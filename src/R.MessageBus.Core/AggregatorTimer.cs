using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Common.Logging;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    public class AggregatorTimer : IAggregatorTimer
    {
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IAggregatorPersistor _aggregatorPersistor;
        private readonly IBusContainer _container;
        private readonly Type _handlerType;
        private Timer _timer;
        private Type _type;
        private Type _genericListType;
        private readonly object _lock = new object();

        public AggregatorTimer(IAggregatorPersistor aggregatorPersistor, IBusContainer container, Type handlerType)
        {
            _handlerType = handlerType;
            _aggregatorPersistor = aggregatorPersistor;
            _container = container;
        }

        public void StartTimer<T>(TimeSpan timeout)
        {
            _type = typeof (T);
            _genericListType = typeof(List<>).MakeGenericType(_type);
            _timer = new Timer(Callback, timeout, timeout, timeout);
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

                    _aggregatorPersistor.RemoveAll(_type.AssemblyQualifiedName);
                }
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}