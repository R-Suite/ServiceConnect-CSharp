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

using Newtonsoft.Json;
using ServiceConnect.Core;
using ServiceConnect.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace ServiceConnect
{
    public class Bus : IBus
    {
        private readonly IBusContainer _container;
        private IConsumer _consumer;
        private bool _startedConsuming;
        private readonly ExpiredTimeoutsPoller _expiredTimeoutsPoller;
        private readonly ILogger _logger;
        private readonly IProcessMessagePipeline _processMessagePipeline;
        private readonly ISendMessagePipeline _sendMessagePipeline;
        private readonly BusState _busState;
        private readonly ConcurrentDictionary<string, Type> _typeLookup = new();
        private static readonly DiagnosticSource _diagnosticSource = new DiagnosticListener("ServiceConnect.Bus");

        public IConfiguration Configuration { get; set; }

        public Bus(IConfiguration configuration)
        {
            _busState = new BusState();

            Configuration = configuration;

            _logger = configuration.GetLogger();
            _container = configuration.GetContainer();
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

                IAggregatorProcessor processor = Configuration.GetAggregatorProcessor(Configuration.GetAggregatorPersistor(), _container, handlerReference.HandlerType);
                if (!_busState.AggregatorProcessors.ContainsKey(handlerReference.MessageType))
                {
                    _busState.AggregatorProcessors.Add(handlerReference.MessageType, processor);
                }

                TimeSpan timeout = (TimeSpan)handlerReference.HandlerType.GetMethod("Timeout").Invoke(aggregator, new object[] { });

                if (timeout != default)
                {
                    MethodInfo processManagerProcessorMethod = processor.GetType().GetMethod("StartTimer");
                    MethodInfo genericProcessManagerProcessorMethod = processManagerProcessorMethod.MakeGenericMethod(handlerReference.MessageType);
                    _ = genericProcessManagerProcessorMethod.Invoke(processor, new object[] { timeout });
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
            Configuration configuration = new();
            action(configuration);

            return new Bus(configuration);
        }

        /// <summary>
        /// Instantiates Bus using the default configuration.
        /// </summary>
        /// <returns>The configured instance of the Bus.</returns>
        public static IBus Initialize()
        {
            Configuration configuration = new();
            return new Bus(configuration);
        }

        public void StartConsuming()
        {
            if (_startedConsuming)
            {
                return;
            }

            StartAggregatorTimers();

            string queueName = Configuration.TransportSettings.QueueName;

            IEnumerable<HandlerReference> instances = _container.GetHandlerTypes();
            IList<string> messageTypes = instances.Where(x => !string.IsNullOrEmpty(x.MessageType.FullName))
                                                        .Select(reference => reference.MessageType.FullName.Replace(".", string.Empty))
                                                        .ToList();

            IConsumer consumer = Configuration.GetConsumer();
            consumer.StartConsuming(queueName, messageTypes, ConsumeMessageEvent, Configuration);
            _consumer = consumer;

            _startedConsuming = true;
        }

        public void Publish<T>(T message, Dictionary<string, string> headers = null) where T : Message
        {
            Publish(message, null, headers);
        }

        public void Publish<T>(T message, string routingKey, Dictionary<string, string> headers = null) where T : Message
        {
            if (_diagnosticSource.IsEnabled("ServiceConnect.Bus.StartPublish"))
            {
                _diagnosticSource.Write("ServiceConnect.Bus.StartPublish", new { Message = message, RoutingKey = routingKey, Headers = headers });
            }

            string messageString = JsonConvert.SerializeObject(message);
            byte[] messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                Envelope envelope = new()
                {
                    Headers = headers == null ? new Dictionary<string, object>() : headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                bool stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
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
                    headers = new Dictionary<string, string> { { "RoutingKey", routingKey } };
                }
            }

            _sendMessagePipeline.ExecutePublishMessagePipeline(typeof(T), messageBytes, headers);

            Type newBaseType = typeof(T).GetTypeInfo().BaseType;
            if (newBaseType != null && newBaseType.Name != typeof(Message).Name)
            {
                MethodInfo publish = GetType().GetMethods().First(m => m.Name == "Publish" && m.GetParameters()[1].Name == "routingKey");
                MethodInfo genericPublish = publish.MakeGenericMethod(newBaseType);
                _ = genericPublish.Invoke(this, new object[] { message, routingKey, (null == headers) ? null : new Dictionary<string, string>(headers) });
            }

            if (_diagnosticSource.IsEnabled("ServiceConnect.Bus.StopPublish"))
            {
                _diagnosticSource.Write("ServiceConnect.Bus.StopPublish", new { Message = message, RoutingKey = routingKey, Headers = headers });
            }
        }

        public IList<TReply> PublishRequest<TRequest, TReply>(TRequest message, int? expectedCount = null, Dictionary<string, string> headers = null, int timeout = 10000) where TRequest : Message
        {
            Guid messageId = Guid.NewGuid();
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(messageId);

            List<TReply> responses = new();
            configuration.EndpointsCount = expectedCount ?? -1;

            Task task = configuration.SetHandler(r => responses.Add((TReply)r));

            lock (_busState.RequestLock)
            {
                _busState.RequestConfigurations[messageId.ToString()] = configuration;
            }

            headers ??= new Dictionary<string, string>();

            headers["RequestMessageId"] = messageId.ToString();

            string messageString = JsonConvert.SerializeObject(message);
            byte[] messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                Envelope envelope = new()
                {
                    Headers = headers == null ? new Dictionary<string, object>() : headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                bool stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
                if (stop)
                {
                    return responses;
                }

                headers = envelope.Headers.ToDictionary(x => x.Key, x => x.Value.ToString());
                messageBytes = envelope.Body;
            }

            _sendMessagePipeline.ExecutePublishMessagePipeline(typeof(TRequest), messageBytes, headers);

            _ = Task.WaitAll(new[] { task }, timeout);

            return responses;
        }

        public void Send<T>(T message, Dictionary<string, string> headers = null) where T : Message
        {
            string messageString = JsonConvert.SerializeObject(message);
            byte[] messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                Envelope envelope = new()
                {
                    Headers = headers == null ? new Dictionary<string, object>() : headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                bool stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
                if (stop)
                {
                    return;
                }

                headers = envelope.Headers.ToDictionary(x => x.Key, x => x.Value.ToString());
                messageBytes = envelope.Body;
            }

            _sendMessagePipeline.ExecuteSendMessagePipeline(typeof(T), messageBytes, headers);
        }

        public void Send<T>(string endPoint, T message, Dictionary<string, string> headers = null) where T : Message
        {
            if (_diagnosticSource.IsEnabled("ServiceConnect.Bus.StartSend"))
            {
                _diagnosticSource.Write("ServiceConnect.Bus.StartSend", new { EndPoint = endPoint, Message = message, Headers = headers });
            }

            string messageString = JsonConvert.SerializeObject(message);
            byte[] messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                Envelope envelope = new()
                {
                    Headers = headers == null ? new Dictionary<string, object>() : headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                bool stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
                if (stop)
                {
                    return;
                }

                headers = envelope.Headers.ToDictionary(x => x.Key, x => x.Value.ToString());
                messageBytes = envelope.Body;
            }

            _sendMessagePipeline.ExecuteSendMessagePipeline(typeof(T), messageBytes, headers, endPoint);

            if (_diagnosticSource.IsEnabled("ServiceConnect.Bus.StopSend"))
            {
                _diagnosticSource.Write("ServiceConnect.Bus.StopSend", new { EndPoint = endPoint, Message = message, Headers = headers });
            }
        }

        public void Send<T>(IList<string> endPoints, T message, Dictionary<string, string> headers = null) where T : Message
        {
            string messageString = JsonConvert.SerializeObject(message);
            byte[] messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                Envelope envelope = new()
                {
                    Headers = headers == null ? new Dictionary<string, object>() : headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                bool stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
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

        public void SendRequest<TRequest, TReply>(TRequest message, Action<TReply> callback, Dictionary<string, string> headers = null) where TRequest : Message where TReply : Message
        {
            SendRequest(null, message, callback, headers);
        }

        public void SendRequest<TRequest, TReply>(IList<string> endPoints, TRequest message, Action<IList<TReply>> callback, Dictionary<string, string> headers = null) where TRequest : Message where TReply : Message
        {
            Guid messageId = Guid.NewGuid();
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(messageId);
            configuration.EndpointsCount = endPoints.Count;

            List<TReply> responses = new();

            _ = configuration.SetHandler(r =>
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

            headers ??= new Dictionary<string, string>();

            headers["RequestMessageId"] = messageId.ToString();

            string messageString = JsonConvert.SerializeObject(message);
            byte[] messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                Envelope envelope = new()
                {
                    Headers = headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                bool stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
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

        public void SendRequest<TRequest, TReply>(string endPoint, TRequest message, Action<TReply> callback, Dictionary<string, string> headers = null) where TRequest : Message where TReply : Message
        {
            Guid messageId = Guid.NewGuid();
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(messageId);
            configuration.EndpointsCount = 1;

            _ = configuration.SetHandler(r => callback((TReply)r));

            lock (_busState.RequestLock)
            {
                _busState.RequestConfigurations[messageId.ToString()] = configuration;
            }

            headers ??= new Dictionary<string, string>();

            headers["RequestMessageId"] = messageId.ToString();

            string messageString = JsonConvert.SerializeObject(message);
            byte[] messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                Envelope envelope = new()
                {
                    Headers = headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                bool stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
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

        public TReply SendRequest<TRequest, TReply>(TRequest message, Dictionary<string, string> headers = null, int timeout = 3000) where TRequest : Message where TReply : Message
        {
            return SendRequest<TRequest, TReply>(default(string), message, headers, timeout);
        }

        public TReply SendRequest<TRequest, TReply>(string endPoint, TRequest message, Dictionary<string, string> headers = null, int timeout = 3000) where TRequest : Message where TReply : Message
        {
            Guid messageId = Guid.NewGuid();
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(messageId);
            configuration.EndpointsCount = 1;

            TReply response = default;

            Task task = configuration.SetHandler(r =>
            {
                response = (TReply)r;
            });

            lock (_busState.RequestLock)
            {
                _busState.RequestConfigurations[messageId.ToString()] = configuration;
            }

            headers ??= new Dictionary<string, string>();

            headers["RequestMessageId"] = messageId.ToString();
            string messageString = JsonConvert.SerializeObject(message);
            byte[] messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                Envelope envelope = new()
                {
                    Headers = headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                bool stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
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

            _ = Task.WaitAll(new[] { task }, timeout);

            return !task.IsCompleted ? throw new TimeoutException() : response;
        }

        public IList<TReply> SendRequest<TRequest, TReply>(IList<string> endPoints, TRequest message, Dictionary<string, string> headers = null, int timeout = 10000) where TRequest : Message where TReply : Message
        {
            Guid messageId = Guid.NewGuid();
            IRequestConfiguration configuration = Configuration.GetRequestConfiguration(messageId);

            List<TReply> responses = new();
            configuration.EndpointsCount = endPoints.Count;

            Task task = configuration.SetHandler(r => responses.Add((TReply)r));

            lock (_busState.RequestLock)
            {
                _busState.RequestConfigurations[messageId.ToString()] = configuration;
            }

            headers ??= new Dictionary<string, string>();

            headers["RequestMessageId"] = messageId.ToString();
            string messageString = JsonConvert.SerializeObject(message);
            byte[] messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                Envelope envelope = new()
                {
                    Headers = headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                bool stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
                if (stop)
                {
                    return responses;
                }

                headers = envelope.Headers.ToDictionary(x => x.Key, x => x.Value.ToString());
                messageBytes = envelope.Body;
            }

            foreach (string endPoint in endPoints)
            {
                _sendMessagePipeline.ExecuteSendMessagePipeline(typeof(TRequest), messageBytes, headers, endPoint);
            }

            _ = Task.WaitAll(new[] { task }, timeout);

            return responses;
        }

        public void Route<T>(T message, IList<string> destinations) where T : Message
        {
            string nextDestination = destinations.First();

            destinations.RemoveAt(0);

            string destionationsJson = JsonConvert.SerializeObject(destinations);

            Dictionary<string, string> headers = new() { { "RoutingSlip", destionationsJson } };
            string messageString = JsonConvert.SerializeObject(message);
            byte[] messageBytes = Encoding.UTF8.GetBytes(messageString);

            if (Configuration.OutgoingFilters != null && Configuration.OutgoingFilters.Count > 0)
            {
                Envelope envelope = new()
                {
                    Headers = headers.ToDictionary(x => x.Key, x => (object)x.Value),
                    Body = messageBytes
                };

                bool stop = ProcessFilters(Configuration.OutgoingFilters, envelope);
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
            string sequenceId = Guid.NewGuid().ToString();
            Dictionary<string, string> headers = new()
            {
                { "MessageType", "ByteStream" },
                { "Start", "" },
                { "SequenceId", sequenceId }
            };
            _ = SendRequest<T, StreamResponseMessage>(endpoint, message, headers, 30000);
            IMessageBusWriteStream stream = Configuration.GetMessageBusWriteStream(Configuration.GetProducer(), endpoint, sequenceId, Configuration);
            return stream;
        }

        private async Task<ConsumeEventResult> ConsumeMessageEvent(byte[] message, string type, IDictionary<string, object> headers)
        {
            if (_diagnosticSource.IsEnabled("ServiceConnect.Bus.StartConsume"))
            {
                _diagnosticSource.Write("ServiceConnect.Bus.StartConsume", new { Message = message, Type = type, Headers = headers });
            }

            ConsumeEventResult result = new()
            {
                Success = true
            };

            ConsumeContext context = new()
            {
                Bus = this,
                Headers = headers,
            };

            string typeName = type.Split(',')[0];
            if (!_typeLookup.TryGetValue(typeName, out Type typeObject))
            {
                typeObject = Type.GetType(typeName) ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName)).FirstOrDefault(t => t != null);
                _ = _typeLookup.TryAdd(typeName, typeObject);
            }

            if (typeObject == null)
            {
                _logger.Warn(string.Format("Could not find type {0} when consuming message.", type));
                return result;
            }

            try
            {
                Envelope envelope = new()
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

                _ = ProcessFilters(Configuration.AfterConsumingFilters, envelope);

                if (headers.ContainsKey("RoutingSlip"))
                {
                    ProcessRoutingSlip(envelope.Body, typeObject, headers);
                }
            }
            catch (Exception ex)
            {
                Configuration.ExceptionHandler?.Invoke(ex);
                result.Success = false;
                result.Exception = ex;
            }
            finally
            {
                if (_diagnosticSource.IsEnabled("ServiceConnect.Bus.StopConsume"))
                {
                    _diagnosticSource.Write("ServiceConnect.Bus.StopConsume", new { Message = message, Type = type, Headers = headers });
                }
            }

            return result;
        }

        private bool ProcessFilters(IEnumerable<Type> filters, Envelope envelope)
        {
            if (filters != null)
            {
                foreach (Type filterType in filters)
                {
                    IFilter filter = (IFilter)_container.GetInstance(filterType);
                    filter.Bus = this;

                    bool stop = !filter.Process(envelope);
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
            string routingSlip = Encoding.UTF8.GetString((byte[])headers["RoutingSlip"]);
            IList<string> destinations = JsonConvert.DeserializeObject<IList<string>>(routingSlip);

            if (null != destinations && destinations.Count > 0)
            {
                object messageObject = JsonConvert.DeserializeObject(Encoding.UTF8.GetString(message), type);

                MethodInfo routeMethod = typeof(Bus).GetMethod("Route");
                MethodInfo genericRouteMethod = routeMethod.MakeGenericMethod(type);
                _ = genericRouteMethod.Invoke(this, new[] { messageObject, destinations });
            }
        }

        private void ProcessStream(byte[] message, Type type, IDictionary<string, object> headers)
        {
            lock (_busState.ByteStreamLock)
            {
                bool start = headers.ContainsKey("Start");
                string sequenceId = Encoding.UTF8.GetString((byte[])headers["SequenceId"]);

                IMessageBusReadStream stream;

                if (start)
                {
                    string requestMessageId = Encoding.UTF8.GetString((byte[])headers["RequestMessageId"]);
                    string sourceAddress = Encoding.UTF8.GetString((byte[])headers["SourceAddress"]);

                    stream = Configuration.GetMessageBusReadStream();
                    stream.CompleteEventHandler = StreamCompleteEventHandler;
                    stream.SequenceId = sequenceId;

                    IStreamProcessor messageHandlerProcessor = _container.GetInstance<IStreamProcessor>(new Dictionary<string, object>
                    {
                        {"container", _container}
                    });
                    MethodInfo handlerProcessorMethod = messageHandlerProcessor.GetType().GetMethod("ProcessMessage");
                    MethodInfo genericHandlerProcessorMethod = handlerProcessorMethod.MakeGenericMethod(type);
                    object messageObject = JsonConvert.DeserializeObject(Encoding.UTF8.GetString(message), type);
                    _ = genericHandlerProcessorMethod.Invoke(messageHandlerProcessor, new[] { messageObject, stream });

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

                    long packetNumber = Convert.ToInt64(Encoding.UTF8.GetString((byte[])headers["PacketNumber"]));
                    bool stop = headers.ContainsKey("Stop");

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
                _ = _busState.ByteStreams.Remove(sequenceId);
            }
        }

        public void StopConsuming()
        {
            _consumer?.Dispose();
        }

        public bool IsConnected()
        {
            return _consumer?.IsConnected() ?? false;
        }

        public void Dispose()
        {
            try
            {
                StopConsuming();
            }
            catch (Exception ex)
            {
                _logger.Error("Error stopping consuming", ex);
            }

            try
            {
                _sendMessagePipeline.Dispose();
            }
            catch (Exception ex)
            {
                _logger.Error("Error disposing producer", ex);
            }

            foreach (IAggregatorProcessor aggregatorProcessor in _busState.AggregatorProcessors.Values)
            {
                try
                {
                    aggregatorProcessor.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.Error("Error disposing aggregator", ex);
                }
            }

            _expiredTimeoutsPoller?.Stop();
        }
    }
}