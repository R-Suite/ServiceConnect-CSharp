using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using log4net;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Interfaces;
using StructureMap;
using StructureMap.Query;

namespace R.MessageBus
{
    public class Bus : IDisposable, IBus
    {
        private static readonly ILog Logger = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private readonly ConcurrentBag<IConsumer> _consumers = new ConcurrentBag<IConsumer>(); 

        public void StartConsuming(string configPath, string endPoint, string queue = null)
        {
            Container.Initialize();

            IEnumerable<InstanceRef> instances = ObjectFactory.Container.Model.AllInstances.Where(i => i.PluginType.Name == typeof(IMessageHandler<>).Name);

            foreach (InstanceRef instanceRef in instances)
            {
                if (instanceRef.ConcreteType != null && !string.IsNullOrEmpty(instanceRef.ConcreteType.Name))
                {
                    Type[] genericArguments = instanceRef.PluginType.GetGenericArguments();
                    
                    //todo: this could be IoCed to support different message bus clients
                    var consumer = new Consumer(configPath, endPoint);
                    consumer.StartConsuming(ConsumeMessageEvent, genericArguments[0].FullName.Replace(".", string.Empty), queue + "." + genericArguments[0].Name);
                    _consumers.Add(consumer);
                }
            }
        }

        private bool ConsumeMessageEvent(byte[] message)
        {
            try
            {
                object objectMessage = SerializationHelper.FromByteArray(message);
                Type t = objectMessage.GetType();
                Type messageHandler = typeof(IMessageHandler<>).MakeGenericType(t);

                IEnumerable<InstanceRef> instances = ObjectFactory.Container.Model.AllInstances.Where(i => i.PluginType == messageHandler);

                foreach (InstanceRef instanceRef in instances)
                {
                    // IF 1 Inherits Process Manager

                    //   if IStartProcessManager

                    //    var handler = ObjectFactory.GetInstance(instanceRef.ConcreteType);
                    //    -> Execute handler
                    //    -> Persists data

                    //  else

                    //    -> Create PM Instance
                    //    -> Lookup PM State in persistent storage by PM Instance CorrelationId
                    //    -> Load PM's data
                    //    -> Execute handler
                    //    -> Persists data if not completed, else remove

                    // ELSE 1

                    try
                    {
                        var handler = ObjectFactory.GetInstance(instanceRef.ConcreteType);
                        messageHandler.GetMethod("Execute").Invoke(handler, new[] { objectMessage });
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(string.Format("Error executing handler. {0}", instanceRef.PluginType.FullName), ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error executing handlera", ex);
                return false;
            }

            return true;
        }

        public void StopConsuming()
        {
            foreach (var consumer in _consumers)
            {
                consumer.StopConsuming();
            }
        }

        public void Dispose()
        {
            StopConsuming();
        }
    }
}