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
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace ServiceConnect
{
    public class Bus : IBus
    {
        private readonly IBusContainer _container;        
        private readonly IProducer _producer;
        private IConsumer _consumer;
        private bool _startedConsuming;
        private readonly ExpiredTimeoutsPoller _expiredTimeoutsPoller;
        private readonly ILogger _logger;
        private readonly IProcessMessagePipeline _processMessagePipeline;
        private readonly ISendMessagePipeline _sendMessagePipeline;
        private readonly BusState _busState;
        private readonly ConcurrentDictionary<string, Type> _typeLookup = new ConcurrentDictionary<string, Type>();


        public IConfiguration Configuration { get; set; }

        public Bus(IConfiguration configuration)
        {
            _busState = new BusState();

            Configuration = configuration;

            _logger = configuration.GetLogger();
            _container = configuration.GetContainer();
            _producer = configuration.GetProducer();
            _processMessagePipeline = configuration.GetProcessMessagePipeline(_busState);
            _sendMessagePipeline = configuration.GetSendMessagePipeline();

            _container.Initialize();

            if (configuration.AddBusToContainer)
            {
                _container.AddBus(this);
            }

            if (configuration.ScanForMesssageHandlers)
            {
                _container.ScanForHandlers();
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
                if (!_busState.AggregatorProcessors.ContainsKey(handlerReference.MessageType))
                {
                    _busState.AggregatorProcessors.Add(handlerReference.MessageType, processor);
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


            IConsumer consumer = Configuration.GetConsumer();
            consumer.StartConsuming(queueName, messageTypes, ConsumeMessageEvent, Configuration);
            _consumer = consumer;
           
            _startedConsuming = true;
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

            _sendMessagePipeline.ExecutePublishMessagePipeline(typeof(T), messageBytes, headers);

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

            lock (_busState.RequestLock)
            {
                _busState.RequestConfigurations[messageId.ToString()] = configuration;
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

            _sendMessagePipeline.ExecutePublishMessagePipeline(typeof(TRequest), messageBytes, headers);

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

            _sendMessagePipeline.ExecuteSendMessagePipeline(typeof(T), messageBytes, headers);
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

            _sendMessagePipeline.ExecuteSendMessagePipeline(typeof(T), messageBytes, headers, endPoint);
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
                _sendMessagePipeline.ExecuteSendMessagePipeline(typeof(T), messageBytes, headers, endPoint);
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

            lock (_busState.RequestLock)
            {
                _busState.RequestConfigurations[messageId.ToString()] = configuration;
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
                _sendMessagePipeline.ExecuteSendMessagePipeline(typeof(TRequest), messageBytes, headers, endPoint);
            }
        }

        public void SendRequest<TRequest, TReply>(string endPoint, TRequest message, Action<TReply> callback, Dictionary<string, string> headers) where TRequest : Message where TReply : Message
        {
            var messageId = Guid.NewGuid();
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(messageId);
            configuration.EndpointsCount = 1;

            configuration.SetHandler(r => callback((TReply)r));

            lock (_busState.RequestLock)
            {
                _busState.RequestConfigurations[messageId.ToString()] = configuration;
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
                _sendMessagePipeline.ExecuteSendMessagePipeline(typeof(TRequest), messageBytes, headers);
            }
            else
            {
                _sendMessagePipeline.ExecuteSendMessagePipeline(typeof(TRequest), messageBytes, headers, endPoint);
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

            lock (_busState.RequestLock)
            {
                _busState.RequestConfigurations[messageId.ToString()] = configuration;
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
                _sendMessagePipeline.ExecuteSendMessagePipeline(typeof(TRequest), messageBytes, headers);
            }
            else
            {
                _sendMessagePipeline.ExecuteSendMessagePipeline(typeof(TRequest), messageBytes, headers, endPoint);
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

            lock (_busState.RequestLock)
            {
                _busState.RequestConfigurations[messageId.ToString()] = configuration;
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
                _sendMessagePipeline.ExecuteSendMessagePipeline(typeof(TRequest), messageBytes, headers, endPoint);
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
            _sendMessagePipeline.ExecuteSendMessagePipeline(typeof(T), messageBytes, headers, nextDestination);
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
            SendRequest<T, StreamResponseMessage>(endpoint, message, headers, 30000);
            var stream = Configuration.GetMessageBusWriteStream(Configuration.GetProducer(), endpoint, sequenceId, Configuration);
            return stream;
        }



        private async Task<ConsumeEventResult> ConsumeMessageEvent(byte[] message, string type, IDictionary<string, object> headers)
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

            var typeName = type.Split(',')[0];
            if (!_typeLookup.TryGetValue(typeName, out var typeObject))
            {
                typeObject = Type.GetType(typeName) ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName)).FirstOrDefault(t => t != null);
                _typeLookup.TryAdd(typeName, typeObject);
            }

            if (typeObject == null)
            {
                _logger.Warn(string.Format("Could not find type {0} when consuming message.", type));
                return result;
            }

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
                
                if (headers.ContainsKey("MessageType") && Encoding.UTF8.GetString((byte[])headers["MessageType"]) == "ByteStream")
                {
                    ProcessStream(envelope.Body, typeObject, headers);
                }
                else
                {
                    await _processMessagePipeline.ExecutePipeline(context, typeObject, envelope);
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
                    var filter = (IFilter)_container.GetInstance(filterType);
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
            lock (_busState.ByteStreamLock)
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
                        _busState.ByteStreams.Add(sequenceId, stream);
                    }

                    Send(sourceAddress, new StreamResponseMessage(Guid.NewGuid()), new Dictionary<string, string> { { "ResponseMessageId", requestMessageId } });
                }
                else
                {
                    if (!_busState.ByteStreams.ContainsKey(sequenceId))
                    {
                        return;
                    }

                    var packetNumber = Convert.ToInt64(Encoding.UTF8.GetString((byte[])headers["PacketNumber"]));
                    var stop = headers.ContainsKey("Stop");

                    stream = _busState.ByteStreams[sequenceId];
                    
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
            lock (_busState.ByteStreamLock)
            {
                _busState.ByteStreams.Remove(sequenceId);
            }
        }
               
        public void StopConsuming()
        {
            if (_consumer != null)
            {
                _consumer.Dispose();
            }
        }

        public bool IsConnected() => _consumer?.IsConnected() ?? false;

        public void Dispose()
        {
            StopConsuming();

            if (null != _producer)
            {
                _producer.Dispose();
            }

            foreach (var aggregatorProcessor in _busState.AggregatorProcessors.Values)
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