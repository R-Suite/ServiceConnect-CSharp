using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using log4net;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Interfaces;

namespace R.MessageBus
{
    public class Bus : IBus
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

        /// <summary>
        /// Instantiates a Bus instance, including any configuration.
        /// </summary>
        /// <param name="action">A lambda that configures that sets the Bus configuration.</param>
        /// <returns>The configured instance of the Bus.</returns>
        public static IBus Initialize(Action<IConfiguration> action)
        {
            var configuration = new Configuration();
            action(configuration);
            return new Bus(configuration);
        }

        /// <summary>
        /// Instantiates Bus using the default configuration.
        /// </summary>
        /// <returns>The configured instance of the Bus.</returns>
        public static IBus Initialize()
        {
            var configuration = new Configuration();
            return new Bus(configuration);
        }

        public void StartConsuming(string queue = null)
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

                IEnumerable<HandlerReference> handlerReferences = _container.GetHandlerTypes(messageHandler);
                //IEnumerable<ProcessManagerReference> processManagerInstances = _container.GetProcessManagerTypes(messageHandler);

                //foreach (ProcessManagerReference processManagerReference in processManagerInstances)
                //{
                //    //   if IStartProcessManager

                //    //    var handler = ObjectFactory.GetInstance(instanceRef.ConcreteType);
                //    //    -> Execute handler
                //    //    -> Persists data

                //    //  else

                //    //    -> Create PM Instance
                //    //    -> Lookup PM State in persistent storage by PM Instance CorrelationId
                //    //    -> Load PM's data
                //    //    -> Execute handler
                //    //    -> Persists data if not completed, else remove

                //}

                foreach (HandlerReference handlerReference in handlerReferences)
                {
                    try
                    {
                        if (handlerReference.HandlerType.IsAssignableFrom(typeof(ProcessManager<>)))
                        {
                        
                        }
                        else
                        {
                            var handler = _container.GetHandlerInstance(handlerReference.HandlerType);
                            messageHandler.GetMethod("Execute").Invoke(handler, new[] { objectMessage });
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(string.Format("Error executing handler. {0}", handlerReference.HandlerType.FullName), ex);
                        throw;
                    }
                    
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error executing handler", ex);
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