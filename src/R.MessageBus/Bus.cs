using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using R.MessageBus.Client.RabbitMQ;
using R.MessageBus.Interfaces;

namespace R.MessageBus
{
    public class Bus : IBus
    {
        private readonly ConcurrentBag<IConsumer> _consumers = new ConcurrentBag<IConsumer>();
        private readonly IBusContainer _container;
        private readonly IJsonMessageSerializer _serializer = new JsonMessageSerializer();

        public IConfiguration Configuration { get; set; }

        public Bus(IConfiguration configuration)
        {
            Configuration = configuration;

            _container = configuration.GetContainer();

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

            foreach (HandlerReference reference in instances)
            {
                string messageTypeName = reference.MessageType.FullName.Replace(".", string.Empty);
                string queueName = Configuration.TransportSettings.Queue.Name;

                IConsumer consumer = Configuration.GetConsumer();
                consumer.StartConsuming(ConsumeMessageEvent, messageTypeName, queueName);
                _consumers.Add(consumer);
            }
        }

        public void Publish<T>(T message) where T : Message
        {
            IProducer producer = Configuration.GetProducer();
            producer.Publish(message);
            producer.Disconnect();
        }

        public void Send<T>(T message) where T : Message
        {
            IProducer producer = Configuration.GetProducer();
            producer.Send(message);
            producer.Disconnect();
        }

        public void Send<T>(string endPoint, T message) where T : Message
        {
            IProducer producer = Configuration.GetProducer();
            producer.Send(endPoint, message);
            producer.Disconnect();
        }

        public void SendRequest<T>(string endPoint, T message, Action<IRequestConfiguration> configureCallback) where T : Message
        {
            //var correlationId = Guid.NewGuid();
            //var configuration = new InlineRequestConfiguration(Configuration, ConsumeMessageEvent, correlationId);
            //configureCallback(configuration);

            //IProducer producer = Configuration.GetProducer();
            //producer.Send(endPoint, message, new Dictionary<string, object> { { "CorrelationId", correlationId } });

            //producer.Disconnect();
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

                Task task = configuration.SetHandler<TReply>(r =>
                {
                    response = r;
                });

                IProducer producer = Configuration.GetProducer();
                if (string.IsNullOrEmpty(endPoint))
                {
                    producer.Send(message, new Dictionary<string, string> { { "CorrelationId", correlationId.ToString() } });
                }
                else
                {
                    producer.Send(endPoint, message, new Dictionary<string, string> { { "CorrelationId", correlationId.ToString() } });
                }
                producer.Disconnect();

                await task;

                return response;
            })().Result;
        }

        private bool ConsumeMessageEvent(byte[] message)
        {
            string messageJson = Encoding.UTF8.GetString(message);
            object objectMessage = _serializer.Deserialize(messageJson);

            bool success = true;

            try
            {
                ProcessMessageHandlers(objectMessage);
                ProcessProcessManagerHandlers(objectMessage);
            }
            catch (Exception)
            {
                success = false;
            }

            return success;
        }

        private void ProcessProcessManagerHandlers(object objectMessage)
        {
            IProcessManagerFinder processManagerFinder = Configuration.GetProcessManagerFinder();
            var processManagerProcessor = _container.GetInstance<IProcessManagerProcessor>(new Dictionary<string, object>
            {
                {"container", _container},
                {"processManagerFinder", processManagerFinder}
            });

            MethodInfo processManagerProcessorMethod = processManagerProcessor.GetType().GetMethod("ProcessMessage");
            MethodInfo genericProcessManagerProcessorMethod = processManagerProcessorMethod.MakeGenericMethod(objectMessage.GetType());
            genericProcessManagerProcessorMethod.Invoke(processManagerProcessor, new[] {objectMessage});
        }

        private void ProcessMessageHandlers(object objectMessage)
        {
            var messageHandlerProcessor = _container.GetInstance<IMessageHandlerProcessor>(new Dictionary<string, object>
            {
                {"container", _container}
            });

            MethodInfo handlerProcessorMethod = messageHandlerProcessor.GetType().GetMethod("ProcessMessage");
            MethodInfo genericHandlerProcessorMethod = handlerProcessorMethod.MakeGenericMethod(objectMessage.GetType());
            genericHandlerProcessorMethod.Invoke(messageHandlerProcessor, new[] {objectMessage});
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