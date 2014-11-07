using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using R.MessageBus.Core;
using R.MessageBus.Interfaces;

namespace R.MessageBus
{
    public class Bus : IBus
    {
        private readonly ConcurrentBag<IConsumer> _consumers = new ConcurrentBag<IConsumer>();
        private readonly IBusContainer _container;
        private readonly IDictionary<string, IRequestConfiguration> _requestConfigurations = new Dictionary<string, IRequestConfiguration>();
        private readonly object _requestLock = new object();
        private static IProducer _producer;

        public IConfiguration Configuration { get; set; }

        public Bus(IConfiguration configuration)
        {
            Configuration = configuration;

            _container = configuration.GetContainer();
            _producer = configuration.GetProducer();

            _container.Initialize();

            if (configuration.AddBusToContainer)
            {
                _container.AddBus(this);
            }

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

        public void StartConsuming()
        {
            IEnumerable<HandlerReference> instances = _container.GetHandlerTypes();

            string queueName = Configuration.TransportSettings.Queue.Name;

            foreach (HandlerReference reference in instances.Where(x => !String.IsNullOrEmpty(x.MessageType.FullName)))
            {
                string messageTypeName = reference.MessageType.FullName.Replace(".", string.Empty);

                IConsumer consumer = Configuration.GetConsumer();
                consumer.StartConsuming(ConsumeMessageEvent, messageTypeName, queueName);
                _consumers.Add(consumer);
            }
        }

        public void Publish<T>(T message) where T : Message
        {
            _producer.Publish(message);
        }

        public void Send<T>(T message) where T : Message
        {
            _producer.Send(message);
        }

        public void Send<T>(string endPoint, T message) where T : Message
        {
            _producer.Send(endPoint, message);
        }
        
        public void SendRequest<TRequest, TReply>(TRequest message, Action<TReply> callback) where TRequest : Message where TReply : Message
        {
            SendRequest(null, message, callback);
        }

        public void SendRequest<TRequest, TReply>(string endPoint, TRequest message, Action<TReply> callback)
            where TRequest : Message
            where TReply : Message
        {
            var correlationId = Guid.NewGuid();
            var messageId = Guid.NewGuid();
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(ConsumeMessageEvent, correlationId, messageId);

            configuration.SetHandler(r => callback((TReply)r));

            lock (_requestLock)
            {
                _requestConfigurations[correlationId.ToString()] = configuration;
            }

            IProducer producer = Configuration.GetProducer();
            if (string.IsNullOrEmpty(endPoint))
            {
                producer.Send(message,
                    new Dictionary<string, string>
                    {
                        {"DestinationAddress", correlationId.ToString()},
                        {"MessageId", messageId.ToString()}
                    });
            }
            else
            {
                producer.Send(endPoint, message,
                    new Dictionary<string, string>
                    {
                        {"DestinationAddress", correlationId.ToString()},
                        {"MessageId", messageId.ToString()}
                    });
            }
            producer.Disconnect();
        }

        public TReply SendRequest<TRequest, TReply>(TRequest message, int timeout)
            where TRequest : Message
            where TReply : Message
        {
            return SendRequest<TRequest, TReply>(null, message, timeout);
        }

        public TReply SendRequest<TRequest, TReply>(string endPoint, TRequest message, int timeout)
            where TRequest : Message
            where TReply : Message
        {
                var correlationId = Guid.NewGuid();
                var messageId = Guid.NewGuid();
                IRequestConfiguration configuration = Configuration.GetRequestConfiguration(ConsumeMessageEvent, correlationId, messageId);

                TReply response = default(TReply);

                Task task = configuration.SetHandler(r =>
                {
                    response = (TReply)r;
                });

                lock (_requestLock)
                {
                    _requestConfigurations[correlationId.ToString()] = configuration;
                }

                if (string.IsNullOrEmpty(endPoint))
                {
                    _producer.Send(message,
                        new Dictionary<string, string>
                        {
                            {"DestinationAddress", correlationId.ToString()},
                            {"MessageId", messageId.ToString()}
                        });
                }
                else
                {
                    _producer.Send(endPoint, message,
                        new Dictionary<string, string>
                        {
                            {"DestinationAddress", correlationId.ToString()},
                            {"MessageId", messageId.ToString()}
                        });
                }

                Task.WaitAll(new[]{ task }, timeout);

                if (!task.IsCompleted)
                {
                    throw new TimeoutException();
                }

                return response;
        }

        private ConsumeEventResult ConsumeMessageEvent(string message, string type, IDictionary<string, object> headers)
        {
            var result = new ConsumeEventResult
            {
                Success = true
            };

            var context = new ConsumeContext
            {
                Bus = this,
                Headers = headers
            };

            Type typeObject = Type.GetType(type);

            try
            {
                ProcessMessageHandlers(message, typeObject, context);
                ProcessProcessManagerHandlers(message, typeObject, context);
                ProcessRequestReplyConfigurations(message, type, context);
            }
            catch (Exception ex)
            {
                if (Configuration.ExceptionHandler != null)
                {
                    Configuration.ExceptionHandler(ex);
                }
                result.Success = false;
                result.Exception = ex;
            }

            return result;
        }

        private void ProcessRequestReplyConfigurations(string message, string type, ConsumeContext context)
        {
            lock (_requestLock)
            {
                string correlationId = Encoding.ASCII.GetString((byte[])context.Headers["DestinationAddress"]);
                string messageId = Encoding.ASCII.GetString((byte[])context.Headers["MessageId"]);
                if (!_requestConfigurations.ContainsKey(correlationId))
                {
                    return;
                }
                IRequestConfiguration requestConfigration = _requestConfigurations[correlationId];
                // Do not process its own request
                if (requestConfigration.RequestMessageId == new Guid(messageId))
                {
                    return;
                }
                requestConfigration.ProcessMessage(message, type);
                var item = _requestConfigurations.First(kvp => kvp.Key == correlationId);
                _requestConfigurations.Remove(item.Key);
            }
        }

        private void ProcessProcessManagerHandlers(string objectMessage, Type type, IConsumeContext context)
        {
            IProcessManagerFinder processManagerFinder = Configuration.GetProcessManagerFinder();
            var processManagerProcessor = _container.GetInstance<IProcessManagerProcessor>(new Dictionary<string, object>
            {
                {"container", _container},
                {"processManagerFinder", processManagerFinder}
            });

            MethodInfo processManagerProcessorMethod = processManagerProcessor.GetType().GetMethod("ProcessMessage");
            MethodInfo genericProcessManagerProcessorMethod = processManagerProcessorMethod.MakeGenericMethod(type);
            genericProcessManagerProcessorMethod.Invoke(processManagerProcessor, new object[] {objectMessage, context});
        }

        private void ProcessMessageHandlers(string objectMessage, Type type, IConsumeContext context)
        {
            var messageHandlerProcessor = _container.GetInstance<IMessageHandlerProcessor>(new Dictionary<string, object>
            {
                {"container", _container}
            });
            MethodInfo handlerProcessorMethod = messageHandlerProcessor.GetType().GetMethod("ProcessMessage");
            MethodInfo genericHandlerProcessorMethod = handlerProcessorMethod.MakeGenericMethod(type);
            genericHandlerProcessorMethod.Invoke(messageHandlerProcessor, new object[] { objectMessage, context });
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
            _producer.Dispose();
        }
    }
}