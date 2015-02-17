using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Common.Logging;
using Newtonsoft.Json;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Core
{
    public class AggregatorProcessor : IAggregatorProcessor
    {
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IAggregatorPersistor _aggregatorPersistor;
        private readonly IBusContainer _container;
        private readonly object _lock = new object();

        public AggregatorProcessor(IAggregatorPersistor aggregatorPersistor, IBusContainer container)
        {
            _aggregatorPersistor = aggregatorPersistor;
            _container = container;
        }

        public void ProcessMessage<T>(string message) where T : Message
        {
            List<HandlerReference> aggregatorInstances = _container.GetHandlerTypes(typeof(Aggregator<T>)).ToList();
            if (aggregatorInstances.Count == 0)
            {
                return;
            }
            if (aggregatorInstances.Count > 1)
            {
                throw new Exception("Can only have one implementation of an aggregator per message type.");
            }
            var aggregatorRef = aggregatorInstances.First();
            object aggregator = _container.GetInstance(aggregatorRef.HandlerType);

            var timeout = (TimeSpan)(aggregatorRef.HandlerType.GetMethod("Timeout").Invoke(aggregator, new object[]{}));
            var batchSize = (int)(aggregatorRef.HandlerType.GetMethod("BatchSize").Invoke(aggregator, new object[]{}));

            var messageObject = (Message)JsonConvert.DeserializeObject(message, typeof(T));

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
                            aggregatorRef.HandlerType.GetMethod("Execute", new[] {typeof (IList<T>)})
                                .Invoke(aggregator, new object[] {messages.Cast<T>().ToList()});
                        }
                        catch (Exception)
                        {
                            Logger.Error("Error executing aggregator execute method");
                            throw;
                        }

                        _aggregatorPersistor.RemoveAll(typeName);
                    }
                }
            }
        }
    }
}