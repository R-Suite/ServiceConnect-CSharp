using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using R.MessageBus.Core;
using R.MessageBus.Interfaces;

namespace R.MessageBus
{
    public class Bus : IBus
    {
        private readonly IBusContainer _container;
        private readonly IDictionary<string, IRequestConfiguration> _requestConfigurations = new Dictionary<string, IRequestConfiguration>();
        private readonly IDictionary<string, IMessageBusReadStream> _byteStreams = new Dictionary<string, IMessageBusReadStream>();
        private readonly object _requestLock = new object();
        private readonly object _byteStreamLock = new object();
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
                ConsumerType = _producer.Type
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

        public void Publish<T>(T message, Dictionary<string, string> headers) where T : Message
        {
            _producer.Publish(message, headers);
        }

        public IList<TReply> PublishRequest<TRequest, TReply>(TRequest message, int? expectedCount, Dictionary<string, string> headers, int timeout) where TRequest : Message
        {
            var messageId = Guid.NewGuid();
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(ConsumeMessageEvent, messageId, typeof(TReply).FullName.Replace(".", string.Empty));

            List<TReply> responses = new List<TReply>();
            configuration.EndpointsCount = expectedCount ?? -1;

            Task task = configuration.SetHandler(r =>
            {
                responses.Add((TReply)r);
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
            
            _producer.Publish(message, headers);

            Task.WaitAll(new[] { task }, timeout);

            return responses;
        }

        public void Send<T>(T message, Dictionary<string, string> headers) where T : Message
        {
            _producer.Send(message, headers);
        }

        public void Send<T>(string endPoint, T message, Dictionary<string, string> headers) where T : Message
        {
            _producer.Send(endPoint, message, headers);
        }
        
        public void Send<T>(IList<string> endPoints, T message, Dictionary<string, string> headers) where T : Message
        {
            foreach (string endPoint in endPoints)
            {
                _producer.Send(endPoint, message, headers);
            }
        }

        public void SendRequest<TRequest, TReply>(TRequest message, Action<TReply> callback, Dictionary<string, string> headers) where TRequest : Message where TReply : Message
        {
            SendRequest(null, message, callback, headers);
        }

        public void SendRequest<TRequest, TReply>(IList<string> endPoints, TRequest message, Action<IList<TReply>> callback, Dictionary<string, string> headers = null) where TRequest : Message where TReply : Message
        {
            var messageId = Guid.NewGuid();
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(ConsumeMessageEvent, messageId, typeof(TReply).FullName.Replace(".", string.Empty));
            configuration.EndpointsCount = endPoints.Count;

            var responses = new List<TReply>();

            configuration.SetHandler(r =>
            {
                responses.Add((TReply)r);
                if (configuration.EndpointsCount == configuration.ProcessedCount)
                {
                    callback(responses);
                }
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

            IProducer producer = Configuration.GetProducer();

            foreach (string endPoint in endPoints)
            {
                producer.Send(endPoint, message, headers);
            }
            producer.Disconnect();
        }

        public void SendRequest<TRequest, TReply>(string endPoint, TRequest message, Action<TReply> callback, Dictionary<string, string> headers) where TRequest : Message where TReply : Message
        {
            var messageId = Guid.NewGuid();
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(ConsumeMessageEvent, messageId, typeof(TReply).FullName.Replace(".", string.Empty));
            configuration.EndpointsCount = 1;

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

        public TReply SendRequest<TRequest, TReply>(TRequest message, Dictionary<string, string> headers, int timeout) where TRequest : Message where TReply : Message
        {
            return SendRequest<TRequest, TReply>(default(string), message, headers, timeout);
        }

        public TReply SendRequest<TRequest, TReply>(string endPoint, TRequest message, Dictionary<string, string> headers, int timeout) where TRequest : Message where TReply : Message
        {
            var messageId = Guid.NewGuid();
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(ConsumeMessageEvent, messageId, typeof(TReply).FullName.Replace(".", string.Empty));
            configuration.EndpointsCount = 1;

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

        public IList<TReply> SendRequest<TRequest, TReply>(IList<string> endPoints, TRequest message, Dictionary<string, string> headers, int timeout) where TRequest : Message where TReply : Message
        {
            var messageId = Guid.NewGuid();
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(ConsumeMessageEvent, messageId, typeof(TReply).FullName.Replace(".", string.Empty));

            List<TReply> responses = new List<TReply>();
            configuration.EndpointsCount = endPoints.Count;

            Task task = configuration.SetHandler(r =>
            {
                responses.Add((TReply)r);
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

            foreach (var endPoint in endPoints)
            {
                _producer.Send(endPoint, message, headers);
            }

            Task.WaitAll(new[] { task }, timeout);
            
            return responses;
        }
        
        public void Route<T>(T message, IList<string> destinations) where T : Message
        {
            string nextDestination = destinations.First();

            destinations.RemoveAt(0);

            var destionationsJson = JsonConvert.SerializeObject(destinations);

            _producer.Send(nextDestination, message, new Dictionary<string, string> { { "RoutingSlip", destionationsJson } });
        }

        public IMessageBusWriteStream CreateStream<T>(string endpoint, T message) where T : Message
        {
            var sequenceId = Guid.NewGuid().ToString();
            var headers = new Dictionary<string, string>
            {
                { "MessageType", "ByteStream" },
                { "Start", "" },
                { "SequenceId", sequenceId }
            };
            SendRequest<T, StreamResponseMessage>(endpoint, message, headers, 10000);
            var stream = new MessageBusWriteStream(Configuration.GetProducer(), endpoint, sequenceId);
            return stream;
        }

        private ConsumeEventResult ConsumeMessageEvent(byte[] message, string type, IDictionary<string, object> headers)
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
                if (Encoding.UTF8.GetString((byte[])headers["MessageType"]) == "ByteStream")
                {
                    ProcessStream(message, typeObject, headers);
                }
                else
                {
                    ProcessMessageHandlers(message, typeObject, context);
                    ProcessProcessManagerHandlers(message, typeObject, context);
                    ProcessRequestReplyConfigurations(message, type, context);

                    if (headers.ContainsKey("RoutingSlip"))
                    {
                        ProcessRoutingSlip(message, typeObject, headers);
                    }
                }
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

        private void ProcessRoutingSlip(byte[] message, Type type, IDictionary<string, object> headers)
        {
            var routingSlip = Encoding.UTF8.GetString((byte[])headers["RoutingSlip"]);
            var destinations = JsonConvert.DeserializeObject<IList<string>>(routingSlip);

            if (null != destinations && destinations.Count > 0)
            {
                object messageObject = JsonConvert.DeserializeObject(Encoding.UTF8.GetString(message), type);

                MethodInfo routeMethod = typeof(Bus).GetMethod("Route");
                MethodInfo genericRouteMethod = routeMethod.MakeGenericMethod(type);
                genericRouteMethod.Invoke(this, new[] { messageObject, destinations });
            }
        }

        private void ProcessStream(byte[] message, Type type, IDictionary<string, object> headers)
        {
            lock (_byteStreamLock)
            {
                var start = headers.ContainsKey("Start");
                var sequenceId = Encoding.UTF8.GetString((byte[]) headers["SequenceId"]);
                
                IMessageBusReadStream stream;

                if (start)
                {
                    var requestMessageId = Encoding.UTF8.GetString((byte[]) headers["RequestMessageId"]);
                    var sourceAddress = Encoding.UTF8.GetString((byte[])headers["SourceAddress"]);
                    
                    stream = new MessageBusReadStream
                    {
                        CompleteEventHandler = StreamCompleteEventHandler,
                        SequenceId = sequenceId
                    };
                    
                    var messageHandlerProcessor = _container.GetInstance<StreamProcessor>(new Dictionary<string, object>
                    {
                        {"container", _container}
                    });
                    MethodInfo handlerProcessorMethod = messageHandlerProcessor.GetType().GetMethod("ProcessMessage");
                    MethodInfo genericHandlerProcessorMethod = handlerProcessorMethod.MakeGenericMethod(type);
                    object messageObject = JsonConvert.DeserializeObject(Encoding.UTF8.GetString(message), type);
                    genericHandlerProcessorMethod.Invoke(messageHandlerProcessor, new[] { messageObject, stream });

                    if (stream.HandlerCount > 0)
                    {
                        _byteStreams.Add(sequenceId, stream);
                    }

                    Send(sourceAddress, new StreamResponseMessage(Guid.NewGuid()), new Dictionary<string, string> { { "ResponseMessageId", requestMessageId } });
                }
                else
                {
                    if (!_byteStreams.ContainsKey(sequenceId))
                    {
                        return;
                    }

                    var packetNumber = Convert.ToInt64(Encoding.UTF8.GetString((byte[])headers["PacketNumber"]));
                    var stop = headers.ContainsKey("Stop");

                    stream = _byteStreams[sequenceId];
                    
                    if (!stop)
                    {
                        stream.Write(message, packetNumber);
                    }
                    else
                    {
                        stream.LastPacketNumber = packetNumber;
                    }
                }
            }
        }

        private void StreamCompleteEventHandler(string sequenceId)
        {
            lock (_byteStreamLock)
            {
                _byteStreams.Remove(sequenceId);
            }
        }

        private void ProcessRequestReplyConfigurations(byte[] byteMessage, string type, ConsumeContext context)
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
                
                requestConfigration.ProcessMessage(Encoding.UTF8.GetString(byteMessage), type);

                if (requestConfigration.ProcessedCount == requestConfigration.EndpointsCount)
                {
                    var item = _requestConfigurations.First(kvp => kvp.Key == messageId);
                    _requestConfigurations.Remove(item.Key);
                }
            }
        }

        private void ProcessProcessManagerHandlers(byte[] objectMessage, Type type, IConsumeContext context)
        {
            IProcessManagerFinder processManagerFinder = Configuration.GetProcessManagerFinder();
            var processManagerProcessor = _container.GetInstance<IProcessManagerProcessor>(new Dictionary<string, object>
            {
                {"container", _container},
                {"processManagerFinder", processManagerFinder}
            });

            MethodInfo processManagerProcessorMethod = processManagerProcessor.GetType().GetMethod("ProcessMessage");
            MethodInfo genericProcessManagerProcessorMethod = processManagerProcessorMethod.MakeGenericMethod(type);
            genericProcessManagerProcessorMethod.Invoke(processManagerProcessor, new object[] {Encoding.UTF8.GetString(objectMessage), context});
        }

        private void ProcessMessageHandlers(byte[] objectMessage, Type type, IConsumeContext context)
        {
            var messageHandlerProcessor = _container.GetInstance<IMessageHandlerProcessor>(new Dictionary<string, object>
            {
                {"container", _container}
            });
            MethodInfo handlerProcessorMethod = messageHandlerProcessor.GetType().GetMethod("ProcessMessage");
            MethodInfo genericHandlerProcessorMethod = handlerProcessorMethod.MakeGenericMethod(type);
            genericHandlerProcessorMethod.Invoke(messageHandlerProcessor, new object[] { Encoding.UTF8.GetString(objectMessage), context });
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