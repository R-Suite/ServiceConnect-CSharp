//Copyright (C) 2015  Timothy Watson, Jakub Pachansky

//This program is free software; you can redistribute it and/or
//modify it under the terms of the GNU General Public License
//as published by the Free Software Foundation; either version 2
//of the License, or (at your option) any later version.

//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//GNU General Public License for more details.

//You should have received a copy of the GNU General Public License
//along with this program; if not, write to the Free Software
//Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;

namespace ServiceConnect
{
    public class Bus : IBus
    {
        private readonly IBusContainer _container;
        private readonly IDictionary<string, IRequestConfiguration> _requestConfigurations = new Dictionary<string, IRequestConfiguration>();
        private readonly IDictionary<string, IMessageBusReadStream> _byteStreams = new Dictionary<string, IMessageBusReadStream>();
        private readonly object _requestLock = new object();
        private readonly object _byteStreamLock = new object();
        private readonly IDictionary<Type, IAggregatorProcessor> _aggregatorProcessors = new Dictionary<Type, IAggregatorProcessor>();
        private readonly IProducer _producer;
        private Timer _timer;
        private bool _startedConsuming;
        private readonly ExpiredTimeoutsPoller _expiredTimeoutsPoller;

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

            if (configuration.AutoStartConsuming)
            {

                StartConsuming();
            }

            if (configuration.EnableProcessManagerTimeouts)
            {
                _expiredTimeoutsPoller = new ExpiredTimeoutsPoller(this);
                _expiredTimeoutsPoller.Start();
            }
        }

        private void StartAggregatorTimers()
        {
            IEnumerable<HandlerReference> instances = _container.GetHandlerTypes().Where(x => x.HandlerType.GetTypeInfo().BaseType != null && 
                                                                                              x.HandlerType.GetTypeInfo().BaseType.GetTypeInfo().IsGenericType && 
                                                                                              x.HandlerType.GetTypeInfo().BaseType.GetGenericTypeDefinition() == typeof(Aggregator<>));
            foreach (HandlerReference handlerReference in instances)
            {
                object aggregator = _container.GetInstance(handlerReference.HandlerType);

                var processor = Configuration.GetAggregatorProcessor(Configuration.GetAggregatorPersistor(), _container, handlerReference.HandlerType);
                if (!_aggregatorProcessors.ContainsKey(handlerReference.MessageType))
                {
                    _aggregatorProcessors.Add(handlerReference.MessageType, processor);
                }

                var timeout = (TimeSpan)(handlerReference.HandlerType.GetMethod("Timeout").Invoke(aggregator, new object[] { }));

                if (timeout != default(TimeSpan))
                {
                    MethodInfo processManagerProcessorMethod = processor.GetType().GetMethod("StartTimer");
                    MethodInfo genericProcessManagerProcessorMethod = processManagerProcessorMethod.MakeGenericMethod(handlerReference.MessageType);
                    genericProcessManagerProcessorMethod.Invoke(processor, new object[] { timeout });
                }
            }
        }

        private void StartHeartbeatTimer()
        {
            var state = new HeartbeatTimerState();
            var timerDelegate = new TimerCallback(CheckStatus);
            _timer = new Timer(timerDelegate, state, new TimeSpan(0, 0, 0), new TimeSpan(0, 0, 30));
        }

        private void CheckStatus(object state)
        {
#if NET451
            var heartbeatState = (HeartbeatTimerState)state;

            if (heartbeatState.CpuCounter == null)
            {
                heartbeatState.CpuCounter = new PerformanceCounter("Process", "% Processor Time", Process.GetCurrentProcess().ProcessName);
            }

            if (heartbeatState.RamCounter == null)
            {
                heartbeatState.RamCounter = new PerformanceCounter("Process", "Working Set", Process.GetCurrentProcess().ProcessName);
            }

            var messageString = JsonConvert.SerializeObject(new HeartbeatMessage(Guid.NewGuid())
            {
                Timestamp = DateTime.UtcNow,
                Location = Configuration.TransportSettings.MachineName,
                Name = Configuration.TransportSettings.QueueName,
                LatestCpu = heartbeatState.CpuCounter.NextValue(),
                LatestMemory = heartbeatState.RamCounter.NextValue(),
                Language = "C#",
                ConsumerType = _producer.Type
            });
            var messageBytes = Encoding.UTF8.GetBytes(messageString);

            _producer.Send(Configuration.TransportSettings.HeartbeatQueueName, typeof(HeartbeatMessage), messageBytes);
#endif
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
            if (_startedConsuming)
                return;

            StartAggregatorTimers();

            string queueName = Configuration.TransportSettings.QueueName;

            IEnumerable<HandlerReference> instances = _container.GetHandlerTypes();
            IList<string> messageTypes = instances.Where(x => !String.IsNullOrEmpty(x.MessageType.FullName))
                                                        .Select(reference => reference.MessageType.FullName.Replace(".", string.Empty))
                                                        .ToList();

            for (int i = 0; i < Configuration.Threads; i++)
            {
                AddConsumer(queueName, messageTypes);
            }

            _startedConsuming = true;
        }

        private void AddConsumer(string queueName, IList<string> messageTypes)
        {
            var consumerPool = Configuration.GetConsumerPool();
            consumerPool.AddConsumer(queueName, messageTypes, ConsumeMessageEvent, Configuration);
        }

        public void Publish<T>(T message, Dictionary<string, string> headers) where T : Message
        {
            Publish(message, null, headers);
        }

        public void Publish<T>(T message, string routingKey, Dictionary<string, string> headers = null) where T : Message
        {
            var messageString = JsonConvert.SerializeObject(message);
            var messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                var envelope = new Envelope
                {
                    Headers = headers == null ? new Dictionary<string, object>() : headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                var stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
                if (stop)
                {
                    return;
                }

                headers = envelope.Headers.ToDictionary(x => x.Key, x => x.Value.ToString());
                messageBytes = envelope.Body;
            }

            // Add routing key to the message header
            if (!string.IsNullOrEmpty(routingKey))
            {
                if (null != headers)
                {
                    if (!headers.ContainsKey("RoutingKey"))
                    {
                        headers["RoutingKey"] = routingKey;
                    }
                }
                else
                {
                    headers = new Dictionary<string, string> {{"RoutingKey", routingKey}};
                }
            }

            _producer.Publish(typeof(T), messageBytes, headers);

            Type newBaseType = typeof(T).GetTypeInfo().BaseType;
            if (newBaseType != null && newBaseType.Name != typeof(Message).Name)
            {
                MethodInfo publish = GetType().GetMethods().First(m => m.Name == "Publish" && m.GetParameters()[1].Name == "routingKey");
                MethodInfo genericPublish = publish.MakeGenericMethod(newBaseType);
                genericPublish.Invoke(this, new object[] { message, routingKey, (null == headers) ? null : new Dictionary<string, string>(headers) });
            }
        }

        public IList<TReply> PublishRequest<TRequest, TReply>(TRequest message, int? expectedCount, Dictionary<string, string> headers, int timeout) where TRequest : Message
        {
            var messageId = Guid.NewGuid();
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(messageId);

            var responses = new List<TReply>();
            configuration.EndpointsCount = expectedCount ?? -1;

            Task task = configuration.SetHandler(r => responses.Add((TReply)r));

            lock (_requestLock)
            {
                _requestConfigurations[messageId.ToString()] = configuration;
            }

            if (headers == null)
            {
                headers = new Dictionary<string, string>();
            }

            headers["RequestMessageId"] = messageId.ToString();

            var messageString = JsonConvert.SerializeObject(message);
            var messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                var envelope = new Envelope
                {
                    Headers = headers == null ? new Dictionary<string, object>() : headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                var stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
                if (stop)
                {
                    return responses;
                }

                headers = envelope.Headers.ToDictionary(x => x.Key, x => x.Value.ToString());
                messageBytes = envelope.Body;
            }

            _producer.Publish(typeof(TRequest), messageBytes, headers);

            Task.WaitAll(new[] { task }, timeout);

            return responses;
        }

        public void Send<T>(T message, Dictionary<string, string> headers) where T : Message
        {
            var messageString = JsonConvert.SerializeObject(message);
            var messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                var envelope = new Envelope
                {
                    Headers = headers == null ? new Dictionary<string, object>() : headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                var stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
                if (stop)
                {
                    return;
                }

                headers = envelope.Headers.ToDictionary(x => x.Key, x => x.Value.ToString());
                messageBytes = envelope.Body;
            }
            _producer.Send(typeof(T), messageBytes, headers);
        }

        public void Send<T>(string endPoint, T message, Dictionary<string, string> headers) where T : Message
        {
            var messageString = JsonConvert.SerializeObject(message);
            var messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                var envelope = new Envelope
                {
                    Headers = headers == null ? new Dictionary<string, object>() : headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                var stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
                if (stop)
                {
                    return;
                }

                headers = envelope.Headers.ToDictionary(x => x.Key, x => x.Value.ToString());
                messageBytes = envelope.Body;
            }
            _producer.Send(endPoint, typeof(T), messageBytes, headers);
        }
        
        public void Send<T>(IList<string> endPoints, T message, Dictionary<string, string> headers) where T : Message
        {
            var messageString = JsonConvert.SerializeObject(message);
            var messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                var envelope = new Envelope
                {
                    Headers = headers == null ? new Dictionary<string, object>() : headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                var stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
                if (stop)
                {
                    return;
                }

                headers = envelope.Headers.ToDictionary(x => x.Key, x => x.Value.ToString());
                messageBytes = envelope.Body;
            }
            foreach (string endPoint in endPoints)
            {
                _producer.Send(endPoint, typeof(T), messageBytes, headers);
            }
        }

        public void SendRequest<TRequest, TReply>(TRequest message, Action<TReply> callback, Dictionary<string, string> headers) where TRequest : Message where TReply : Message
        {
            SendRequest(null, message, callback, headers);
        }

        public void SendRequest<TRequest, TReply>(IList<string> endPoints, TRequest message, Action<IList<TReply>> callback, Dictionary<string, string> headers = null) where TRequest : Message where TReply : Message
        {
            var messageId = Guid.NewGuid();
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(messageId);
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

            var messageString = JsonConvert.SerializeObject(message);
            var messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                var envelope = new Envelope
                {
                    Headers = headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                var stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
                if (stop)
                {
                    return;
                }

                headers = envelope.Headers.ToDictionary(x => x.Key, x => x.Value.ToString());
                messageBytes = envelope.Body;
            }

            foreach (string endPoint in endPoints)
            {
                _producer.Send(endPoint, typeof(TRequest), messageBytes, headers);
            }
        }

        public void SendRequest<TRequest, TReply>(string endPoint, TRequest message, Action<TReply> callback, Dictionary<string, string> headers) where TRequest : Message where TReply : Message
        {
            var messageId = Guid.NewGuid();
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(messageId);
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

            var messageString = JsonConvert.SerializeObject(message);
            var messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                var envelope = new Envelope
                {
                    Headers = headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                var stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
                if (stop)
                {
                    return;
                }

                headers = envelope.Headers.ToDictionary(x => x.Key, x => x.Value.ToString());
                messageBytes = envelope.Body;
            }

            if (string.IsNullOrEmpty(endPoint))
            {
                _producer.Send(typeof(TRequest), messageBytes, headers);
            }
            else
            {
                _producer.Send(endPoint, typeof(TRequest), messageBytes, headers);
            }
        }

        public TReply SendRequest<TRequest, TReply>(TRequest message, Dictionary<string, string> headers, int timeout) where TRequest : Message where TReply : Message
        {
            return SendRequest<TRequest, TReply>(default(string), message, headers, timeout);
        }

        public TReply SendRequest<TRequest, TReply>(string endPoint, TRequest message, Dictionary<string, string> headers, int timeout) where TRequest : Message where TReply : Message
        {
            var messageId = Guid.NewGuid();
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(messageId);
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
            var messageString = JsonConvert.SerializeObject(message);
            var messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                var envelope = new Envelope
                {
                    Headers = headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                var stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
                if (stop)
                {
                    return response;
                }

                headers = envelope.Headers.ToDictionary(x => x.Key, x => x.Value.ToString());
                messageBytes = envelope.Body;
            }

            if (string.IsNullOrEmpty(endPoint))
            {
                _producer.Send(typeof(TRequest), messageBytes, headers);
            }
            else
            {
                _producer.Send(endPoint, typeof(TRequest), messageBytes, headers);
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
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(messageId);

            var responses = new List<TReply>();
            configuration.EndpointsCount = endPoints.Count;

            Task task = configuration.SetHandler(r => responses.Add((TReply)r));

            lock (_requestLock)
            {
                _requestConfigurations[messageId.ToString()] = configuration;
            }

            if (headers == null)
            {
                headers = new Dictionary<string, string>();
            }

            headers["RequestMessageId"] = messageId.ToString();
            var messageString = JsonConvert.SerializeObject(message);
            var messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                var envelope = new Envelope
                {
                    Headers = headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                var stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
                if (stop)
                {
                    return responses;
                }

                headers = envelope.Headers.ToDictionary(x => x.Key, x => x.Value.ToString());
                messageBytes = envelope.Body;
            }
            
            foreach (var endPoint in endPoints)
            {
                _producer.Send(endPoint, typeof(TRequest), messageBytes, headers);
            }

            Task.WaitAll(new[] { task }, timeout);
            
            return responses;
        }
        
        public void Route<T>(T message, IList<string> destinations) where T : Message
        {
            string nextDestination = destinations.First();

            destinations.RemoveAt(0);

            var destionationsJson = JsonConvert.SerializeObject(destinations);

            var headers = new Dictionary<string, string> {{"RoutingSlip", destionationsJson}};
            var messageString = JsonConvert.SerializeObject(message);
            var messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                var envelope = new Envelope
                {
                    Headers = headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                var stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
                if (stop)
                {
                    return;
                }

                headers = envelope.Headers.ToDictionary(x => x.Key, x => x.Value.ToString());
                messageBytes = envelope.Body;
            }
            _producer.Send(nextDestination, typeof(T), messageBytes, headers);
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
            var stream = Configuration.GetMessageBusWriteStream(Configuration.GetProducer(), endpoint, sequenceId, Configuration);
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
                Headers = headers,
            };

            Type typeObject = null;

#if NETSTANDARD1_6
            typeObject = Type.GetType(type);
            if (typeObject == null)
            {
                var assemblies = Microsoft.Extensions.DependencyModel.DependencyContext.Default.RuntimeLibraries;
                foreach (var assembly in assemblies)
                {
                    try
                    {
                        var asm = Assembly.Load(new AssemblyName(assembly.Name));
                        typeObject = asm.GetTypes().Where(t => t.AssemblyQualifiedName == type).FirstOrDefault();

                        if (null != typeObject)
                            break;
                    }
                    catch (Exception)
                    {}
                }
            }
#else
            typeObject = Type.GetType(type) ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(type)).FirstOrDefault(t => t != null);
#endif

            try
            {
                var envelope = new Envelope
                {
                    Headers = headers,
                    Body = message
                };

                bool stop = ProcessFilters(Configuration.BeforeConsumingFilters, envelope);
                if (stop)
                {
                    return result;
                }
                
                if (Encoding.UTF8.GetString((byte[])headers["MessageType"]) == "ByteStream")
                {
                    ProcessStream(envelope.Body, typeObject, headers);
                }
                else
                {
                    ProcessMessageHandlers(envelope.Body, typeObject, context);
                    ProcessProcessManagerHandlers(envelope.Body, typeObject, context);
                    ProcessAggregatorHandlers(envelope.Body, typeObject);
                    ProcessRequestReplyConfigurations(envelope.Body, type, context);
                }

                ProcessFilters(Configuration.AfterConsumingFilters, envelope);

                if (headers.ContainsKey("RoutingSlip"))
                {
                    ProcessRoutingSlip(envelope.Body, typeObject, headers);
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

        private bool ProcessFilters(IEnumerable<Type> filters, Envelope envelope)
        {
            if (filters != null)
            {
                foreach (Type filterType in filters)
                {
                    var filter = (IFilter)Activator.CreateInstance(filterType);
                    filter.Bus = this;

                    var stop = !filter.Process(envelope);
                    if (stop)
                    {
                        return true;
                    }
                }
            }
            return false;
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

                    stream = Configuration.GetMessageBusReadStream();
                    stream.CompleteEventHandler = StreamCompleteEventHandler;
                    stream.SequenceId = sequenceId;
                    
                    var messageHandlerProcessor = _container.GetInstance<IStreamProcessor>(new Dictionary<string, object>
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
                
                string messageId = Encoding.UTF8.GetString((byte[])context.Headers["ResponseMessageId"]);
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

        private void ProcessAggregatorHandlers(byte[] objectMessage, Type type)
        {
            if (_aggregatorProcessors.ContainsKey(type))
            {
                IAggregatorProcessor aggregatorProcessor = _aggregatorProcessors[type];

                MethodInfo aggregatorProcessorMethod = aggregatorProcessor.GetType().GetMethod("ProcessMessage");
                MethodInfo genericAggregatorProcessorMethod = aggregatorProcessorMethod.MakeGenericMethod(type);
                genericAggregatorProcessorMethod.Invoke(aggregatorProcessor, new object[] { Encoding.UTF8.GetString(objectMessage) });
            }
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
            var consumerPool = Configuration.GetConsumerPool();
            consumerPool.Dispose();
        }

        public void Dispose()
        {
            StopConsuming();

            if (null != _producer)
            {
                _producer.Dispose();
            }

            if (Configuration.TransportSettings.AuditingEnabled && null != _timer)
            {
                _timer.Dispose();
            }

            foreach (var aggregatorProcessor in _aggregatorProcessors.Values)
            {
                aggregatorProcessor.Dispose();
            }

            if (null != _expiredTimeoutsPoller)
            {
                _expiredTimeoutsPoller.Stop();
            }
        }
    }
}