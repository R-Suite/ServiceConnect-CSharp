using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using log4net.Util;
using R.MessageBus.Core;
using R.MessageBus.Interfaces;

namespace R.MessageBus
{
    public class Bus : IBus
    {
        private readonly ConcurrentBag<IConsumer> _consumers = new ConcurrentBag<IConsumer>();
        private readonly IBusContainer _container;
        private readonly IMessageSerializer _serializer;
        private readonly IDictionary<string, IRequestConfiguration> _requestConfigurations = new Dictionary<string, IRequestConfiguration>();
        private readonly object _requestLock = new object();
        private static IProducer _producer;

        public IConfiguration Configuration { get; set; }

        public Bus(IConfiguration configuration)
        {
            Configuration = configuration;

            _container = configuration.GetContainer();
            _producer = Configuration.GetProducer();
            _serializer = Configuration.GetSerializer();

            _container.Initialize();
            _container.AddBus(this);

            if (Configuration.ScanForMesssageHandlers)
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

        public void SendRequest<TRequest, TReply>(string endPoint, TRequest message, Action<TReply> callback) where TRequest : Message where TReply : Message
        {
            var correlationId = Guid.NewGuid();
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(ConsumeMessageEvent, correlationId);

            configuration.SetHandler(r => callback((TReply)r));

            lock (_requestLock)
            {
                _requestConfigurations[correlationId.ToString()] = configuration;
            }

            IProducer producer = Configuration.GetProducer();
            if (string.IsNullOrEmpty(endPoint))
            {
                producer.Send(message, new Dictionary<string, string> { { "DestinationAddress", correlationId.ToString() } });
            }
            else
            {
                producer.Send(endPoint, message, new Dictionary<string, string> { { "DestinationAddress", correlationId.ToString() } });
            }
            producer.Disconnect();
        }

        public TReply SendRequest<TRequest, TReply>(TRequest message) where TRequest : Message where TReply : Message
        {
            return SendRequest<TRequest, TReply>(null, message);
        }
    
        public TReply SendRequest<TRequest, TReply>(string endPoint, TRequest message) where TRequest : Message where TReply : Message
        {
            return new Func<Task<TReply>>(async () =>
            {
                var correlationId = Guid.NewGuid();
                IRequestConfiguration configuration = Configuration.GetRequestConfiguration(ConsumeMessageEvent, correlationId);

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
                    _producer.Send(message, new Dictionary<string, string> { { "DestinationAddress", correlationId.ToString() } });
                }
                else
                {
                    _producer.Send(endPoint, message, new Dictionary<string, string> { { "DestinationAddress", correlationId.ToString() } });
                }

                await task;

                return response;
            })().Result;
        }

        private ConsumeEventResult ConsumeMessageEvent(byte[] message, IDictionary<string, object> headers)
        {
            var result = new ConsumeEventResult
            {
                Success = true
            };

            object objectMessage = null;
            try
            {
                string messageJson = Encoding.UTF8.GetString(message);
                objectMessage = _serializer.Deserialize(messageJson);
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

            if (result.Success == false)
            {
                return result;
            }

            var context = new ConsumeContext
            {
                Bus = this,
                Headers = headers
            };

            try
            {
                ProcessMessageHandlers(objectMessage, context);
                ProcessProcessManagerHandlers(objectMessage, context);
                ProcessRequestReplyConfigurations(objectMessage, context);
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

        private void ProcessRequestReplyConfigurations(object objectMessage, ConsumeContext context)
        {
            lock (_requestLock)
            {
                string correlationId = Encoding.ASCII.GetString((byte[])context.Headers["DestinationAddress"]);
                if (!_requestConfigurations.ContainsKey(correlationId))
                {
                    return;
                }
                IRequestConfiguration requestConfigration = _requestConfigurations[correlationId];
                requestConfigration.ProcessMessage(objectMessage);
                var item = _requestConfigurations.First(kvp => kvp.Key == correlationId);
                _requestConfigurations.Remove(item.Key);
            }
        }

        private void ProcessProcessManagerHandlers(object objectMessage, IConsumeContext context)
        {
            IProcessManagerFinder processManagerFinder = Configuration.GetProcessManagerFinder();
            var processManagerProcessor = _container.GetInstance<IProcessManagerProcessor>(new Dictionary<string, object>
            {
                {"container", _container},
                {"processManagerFinder", processManagerFinder}
            });

            MethodInfo processManagerProcessorMethod = processManagerProcessor.GetType().GetMethod("ProcessMessage");
            MethodInfo genericProcessManagerProcessorMethod = processManagerProcessorMethod.MakeGenericMethod(objectMessage.GetType());
            genericProcessManagerProcessorMethod.Invoke(processManagerProcessor, new[] {objectMessage, context});
        }

        private void ProcessMessageHandlers(object objectMessage, IConsumeContext context)
        {
            var messageHandlerProcessor = _container.GetInstance<IMessageHandlerProcessor>(new Dictionary<string, object>
            {
                {"container", _container}
            });

            MethodInfo handlerProcessorMethod = messageHandlerProcessor.GetType().GetMethod("ProcessMessage");
            MethodInfo genericHandlerProcessorMethod = handlerProcessorMethod.MakeGenericMethod(objectMessage.GetType());
            genericHandlerProcessorMethod.Invoke(messageHandlerProcessor, new[] {objectMessage, context});
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