using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using log4net;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Interfaces;

namespace R.MessageBus
{
    public class Bus : IBus
    {
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly ConcurrentBag<IConsumer> _consumers = new ConcurrentBag<IConsumer>();
        private readonly IBusContainer _container;
        private readonly IJsonMessageSerializer _serializer = new JsonMessageSerializer();
        private readonly IProcessManagerFinder _processManagerFinder;

        public IConfiguration Configuration { get; set; }

        public Bus(IConfiguration configuration)
        {
            Configuration = configuration;

            _container = configuration.GetContainer();
            _processManagerFinder = configuration.GetProcessManagerFinder();

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
                string messageJson = Encoding.UTF8.GetString(message);
                object objectMessage = _serializer.Deserialize(messageJson);
                Type messageType = objectMessage.GetType();
                Type messageHandler = typeof(IMessageHandler<>).MakeGenericType(messageType);
                Type startProcessManagerType = typeof(IStartProcessManager<>).MakeGenericType(messageType);

                List<HandlerReference> handlerReferences = _container.GetHandlerTypes(messageHandler).ToList();
                List<HandlerReference> processManagerInstances = _container.GetHandlerTypes(startProcessManagerType).ToList();

                foreach (HandlerReference processManagerInstance in processManagerInstances)
                {
                    try
                    {
                        // Create instance of the project manager
                        object processManager = _container.GetHandlerInstance(processManagerInstance.HandlerType);
                        
                        // Get Data Type
                        Type dataType = processManagerInstance.HandlerType.BaseType.GetGenericArguments()[0];

                        // Create new instance 
                        var data = (IProcessManagerData)Activator.CreateInstance(dataType);

                        // Set data on process manager
                        PropertyInfo prop = processManagerInstance.HandlerType.GetProperty("Data");
                        prop.SetValue(processManager, data, null);

                        // Execute process manager execute method
                        processManagerInstance.HandlerType.GetMethod("Execute", new []{ messageType }).Invoke(processManager, new[] { objectMessage });

                        // Get data after execute has finished
                        data = (IProcessManagerData)prop.GetValue(processManager);
                        
                        // Persist data
                        _processManagerFinder.InsertData(data);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(string.Format("Error executing process manager start method. {0}", processManagerInstance.HandlerType.FullName), ex);
                        throw;
                    }
                }

                foreach (HandlerReference handlerReference in handlerReferences.Where(x => processManagerInstances.All(y => y.HandlerType != x.HandlerType)))
                {
                    try
                    {
                        if (handlerReference.HandlerType.BaseType != null && handlerReference.HandlerType.BaseType.Name == typeof(ProcessManager<>).Name)
                        {
                            // Create instance of the project manager
                            object processManager = _container.GetHandlerInstance(handlerReference.HandlerType);

                            // Set Process Manager Finder property
                            PropertyInfo processManagerFinderProp = handlerReference.HandlerType.GetProperty("ProcessManagerFinder");
                            processManagerFinderProp.SetValue(processManager, _processManagerFinder, null);

                            // Execute FindProcessManagerData
                            object persistanceData = handlerReference.HandlerType.GetMethod("FindProcessManagerData").Invoke(processManager, new[] { objectMessage });



                            // Set data property value
                            PropertyInfo prop = handlerReference.HandlerType.GetProperty("Data");
                            prop.SetValue(processManager, data, null);

                            // Execute handler
                            handlerReference.HandlerType.GetMethod("Execute", new[] { messageType }).Invoke(processManager, new[] { objectMessage });

                            // Get Complete property value
                            var completeProperty = handlerReference.HandlerType.GetProperty("Complete");
                            var isComplete = (bool)completeProperty.GetValue(processManager);

                            if (isComplete)
                            {
                                // Delete if the process manager is complete
                                _processManagerFinder.GetType().GetMethod("DeleteData").Invoke(_processManagerFinder, new[] { data });
                            }
                            else
                            {
                                // Otherwise update
                                _processManagerFinder.GetType().GetMethod("UpdateData").Invoke(_processManagerFinder, new[] { data });
                            }
                        }
                        else
                        {
                            object handler = _container.GetHandlerInstance(handlerReference.HandlerType);
                            messageHandler.GetMethod("Execute", new[] { messageType }).Invoke(handler, new[] { objectMessage });
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