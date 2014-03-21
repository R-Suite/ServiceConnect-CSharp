using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using log4net;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Interfaces;

namespace R.MessageBus
{
    public class Bus : IDisposable, IBus
    {
        private static readonly ILog Logger = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private readonly ConcurrentBag<IConsumer> _consumers = new ConcurrentBag<IConsumer>();
        private readonly IBusContainer _container;

        public IConfiguration Configuration { get; set; }

        public Bus(IConfiguration configuration)
        {
            Configuration = configuration;

            _container = configuration.GetContainer();

            if (configuration.ScanForMesssageHandlers)
            {
                _container.ScanForHandlers();
            }
        }

        public static IBus Initialize(Action<IConfiguration> action)
        {
            var configuration = new Configuration();
            action(configuration);
            return new Bus(configuration);
        }

        public static IBus Initialize()
        {
            var configuration = new Configuration();
            return new Bus(configuration);
        }

        public void StartConsuming(string configPath, string endPoint, string queue = null)
        {
            IEnumerable<HandlerReference> instances = _container.GetHandlerTypes(); 

            foreach (HandlerReference reference in instances)
            {
                string routingKey = reference.MessageType.FullName.Replace(".", string.Empty);
                string queueName = queue + "." + reference.MessageType.Name;

                IConsumer consumer = Configuration.GetConsumer();
                consumer.StartConsuming(ConsumeMessageEvent, routingKey, queueName);
                _consumers.Add(consumer);
            }
        }

        private bool ConsumeMessageEvent(byte[] message)
        {
            try
            {
                object objectMessage = SerializationHelper.FromByteArray(message);
                Type t = objectMessage.GetType();
                Type messageHandler = typeof(IMessageHandler<>).MakeGenericType(t);

                IEnumerable<HandlerReference> instances = _container.GetHandlerTypes(messageHandler); ;

                foreach (HandlerReference instance in instances)
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
                        var handler = _container.GetHandlerInstance(instance.HandlerType); 
                        messageHandler.GetMethod("Execute").Invoke(handler, new[] { objectMessage });
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(string.Format("Error executing handler. {0}", instance.HandlerType.FullName), ex);
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