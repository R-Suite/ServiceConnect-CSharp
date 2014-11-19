using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using R.MessageBus.Core;
using R.MessageBus.Interfaces;

namespace R.MessageBus
{
    public class Bus : IBus
    {
        private readonly IBusContainer _container;
        private readonly IDictionary<string, IRequestConfiguration> _requestConfigurations = new Dictionary<string, IRequestConfiguration>();
        private readonly object _requestLock = new object();
        private static IProducer _producer;
        private Timer _timer;
        private IConsumer _consumer;

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

            if (configuration.TransportSettings.AuditingEnabled)
            {
                StartHeartbeatTimer();
            }
        }

        private void StartHeartbeatTimer()
        {
            var state = new HeartbeatTimerState
            {
                CpuCounter = new PerformanceCounter("Process", "% Processor Time", Process.GetCurrentProcess().ProcessName),
                RamCounter = new PerformanceCounter("Process", "Working Set", Process.GetCurrentProcess().ProcessName)
            };

            var timerDelegate = new TimerCallback(CheckStatus);
            _timer = new Timer(timerDelegate, state, new TimeSpan(0, 0, 0), new TimeSpan(0, 0, 30));
        }

        private void CheckStatus(object state)
        {
            var heartbeatState = (HeartbeatTimerState) state;

            _producer.Send(Configuration.TransportSettings.HeartbeatQueueName, new HeartbeatMessage(Guid.NewGuid())
            {
                Timestamp = DateTime.UtcNow,
                Location = Configuration.TransportSettings.MachineName,
                Name = Configuration.TransportSettings.Queue.Name,
                LatestCpu = heartbeatState.CpuCounter.NextValue(),
                LatestMemory = heartbeatState.RamCounter.NextValue(),
                Language = "C#",
                ConsumerType = _consumer.Type
            });
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

            _consumer = Configuration.GetConsumer();
            _consumer.StartConsuming(ConsumeMessageEvent, queueName);

            foreach (HandlerReference reference in instances.Where(x => !String.IsNullOrEmpty(x.MessageType.FullName)))
            {
                string messageTypeName = reference.MessageType.FullName.Replace(".", string.Empty);
                _consumer.ConsumeMessageType(messageTypeName);
            }
        }

        public void Publish<T>(T message) where T : Message
        {
            Publish(message, null);
        }

        public void Publish<T>(T message, Dictionary<string, string> headers) where T : Message
        {
            _producer.Publish(message, headers);
        }

        public void Send<T>(T message) where T : Message
        {
            Send(message, null);
        }

        public void Send<T>(T message, Dictionary<string, string> headers) where T : Message
        {
            _producer.Send(message, headers);
        }

        public void Send<T>(string endPoint, T message) where T : Message
        {
            Send(endPoint, message, null);
        }

        public void Send<T>(string endPoint, T message, Dictionary<string, string> headers) where T : Message
        {
            _producer.Send(endPoint, message, headers);
        }

        public void SendRequest<TRequest, TReply>(TRequest message, Action<TReply> callback) where TRequest : Message where TReply : Message
        {
            SendRequest(message, callback, null);
        }

        public void SendRequest<TRequest, TReply>(TRequest message, Action<TReply> callback, Dictionary<string, string> headers) where TRequest : Message where TReply : Message
        {
            SendRequest(null, message, callback, headers);
        }

        public void SendRequest<TRequest, TReply>(string endPoint, TRequest message, Action<TReply> callback) where TRequest : Message where TReply : Message
        {
            SendRequest(endPoint, message, callback, null);
        }

        public void SendRequest<TRequest, TReply>(string endPoint, TRequest message, Action<TReply> callback, Dictionary<string, string> headers) where TRequest : Message where TReply : Message
        {
            var messageId = Guid.NewGuid();
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(ConsumeMessageEvent, messageId, typeof(TReply).FullName.Replace(".", string.Empty));

            configuration.SetHandler(r => callback((TReply)r));

            lock (_requestLock)
            {
                _requestConfigurations[messageId.ToString()] = configuration;
            }

            if (headers == null)
            {
                headers = new Dictionary<string, string>();
            }

            headers["RequestMessageId"] = messageId.ToString();

            IProducer producer = Configuration.GetProducer();
            if (string.IsNullOrEmpty(endPoint))
            {
                producer.Send(message, headers);
            }
            else
            {
                producer.Send(endPoint, message, headers);
            }
            producer.Disconnect();
        }

        public TReply SendRequest<TRequest, TReply>(TRequest message, int timeout) where TRequest : Message where TReply : Message
        {
            return SendRequest<TRequest, TReply>(message, null, timeout);
        }

        public TReply SendRequest<TRequest, TReply>(TRequest message, Dictionary<string, string> headers, int timeout) where TRequest : Message where TReply : Message
        {
            return SendRequest<TRequest, TReply>(null, message, headers, timeout);
        }

        public TReply SendRequest<TRequest, TReply>(string endPoint, TRequest message, int timeout) where TRequest : Message where TReply : Message
        {
            return SendRequest<TRequest, TReply>(endPoint, message, null, timeout);
        }

        public TReply SendRequest<TRequest, TReply>(string endPoint, TRequest message, Dictionary<string, string> headers, int timeout) where TRequest : Message where TReply : Message
        {
            var messageId = Guid.NewGuid();
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(ConsumeMessageEvent, messageId, typeof(TReply).FullName.Replace(".", string.Empty));

            TReply response = default(TReply);

            Task task = configuration.SetHandler(r =>
            {
                response = (TReply)r;
            });

            lock (_requestLock)
            {
                _requestConfigurations[messageId.ToString()] = configuration;
            }

            if (headers == null)
            {
                headers = new Dictionary<string, string>();
            }

            headers["RequestMessageId"] = messageId.ToString();

            if (string.IsNullOrEmpty(endPoint))
            {
                _producer.Send(message, headers);
            }
            else
            {
                _producer.Send(endPoint, message, headers);
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
                if (!context.Headers.ContainsKey("ResponseMessageId"))
                {
                    return;
                }
                
                string messageId = Encoding.ASCII.GetString((byte[])context.Headers["ResponseMessageId"]);
                if (!_requestConfigurations.ContainsKey(messageId))
                {
                    return;
                }
                IRequestConfiguration requestConfigration = _requestConfigurations[messageId];
                
                requestConfigration.ProcessMessage(message, type);
                var item = _requestConfigurations.First(kvp => kvp.Key == messageId);
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
            _consumer.StopConsuming();
        }

        public void Dispose()
        {
            StopConsuming();
            _producer.Dispose();

            if (Configuration.TransportSettings.AuditingEnabled)
            {
                _timer.Dispose();
            }
        }
    }
}