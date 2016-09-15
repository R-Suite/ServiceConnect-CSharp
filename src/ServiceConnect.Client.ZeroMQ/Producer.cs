using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Common.Logging;
using Newtonsoft.Json;
using ServiceConnect.Interfaces;
using ZeroMQ;

namespace ServiceConnect.Client.ZeroMQ
{
    /// <summary>
    /// ***************
    /// Experimental - NOT for production use
    /// ***************
    /// </summary>
    public class Producer : IProducer
    {
        private readonly ITransportSettings _transportSettings;
        private readonly IDictionary<string, IList<string>> _queueMappings;
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly Object _lock = new Object();
        private readonly ZSocket _publisher;
        private readonly ZContext _publishContext;
        private ZSocket _sender;
        private ZContext _senderContext;
        private string _senderEndpoint;

        public Producer(ITransportSettings transportSettings, IDictionary<string, IList<string>> queueMappings)
        {
            _transportSettings = transportSettings;
            _queueMappings = queueMappings;

            if (_transportSettings.ClientSettings.ContainsKey("PublisherHost"))
            {
                _publishContext = new ZContext();
                _publisher = new ZSocket(_publishContext, ZSocketType.PUB);
                _publisher.Linger = TimeSpan.FromMilliseconds(1);
                _publisher.Bind(_transportSettings.ClientSettings["PublisherHost"].ToString());
            }
        }

        public void Dispose()
        {
            if (null != _publisher)
            {
                _publisher.Dispose();
                _publishContext.Dispose();
            }

            if (null != _sender)
            {
                _sender.Dispose();
                _senderContext.Dispose();
            }
        }

        public void Publish(Type type, byte[] message, Dictionary<string, string> headers = null)
        {
            Publish(type, message, null, headers);
        }

        public void Publish(Type type, byte[] message, string routingKey, Dictionary<string, string> headers = null)
        {
            Dictionary<string, object> messageHeaders = GetHeaders(type, headers, _transportSettings.QueueName, "Publish");
            var serializedHeaders = JsonConvert.SerializeObject(messageHeaders);

            var msg = new ZMessage();
            msg.Append(new ZFrame(type.FullName.Replace(".", string.Empty)));
            msg.Append(new ZFrame(serializedHeaders));
            msg.Append(new ZFrame(message));

            _publisher.SendMessage(msg);
        }

        public void Send(Type type, byte[] message, Dictionary<string, string> headers = null)
        {
            IList<string> endPoints = _queueMappings[type.FullName];

            foreach (string endPoint in endPoints)
            {
                Dictionary<string, object> messageHeaders = GetHeaders(type, headers, endPoint, "Send");
                var serializedHeaders = JsonConvert.SerializeObject(messageHeaders);

                var msg = new ZMessage();
                msg.Append(new ZFrame(serializedHeaders));
                msg.Append(new ZFrame(message));

                // Send
                _sender.SendMessage(msg);
            }
        }

        public void Send(string endPoint, Type type, byte[] message, Dictionary<string, string> headers = null)
        {
            lock (_lock)
            {
                Dictionary<string, object> messageHeaders = GetHeaders(type, headers, endPoint, "Send");
                var serializedHeaders = JsonConvert.SerializeObject(messageHeaders);

                var msg = new ZMessage();
                msg.Append(new ZFrame(serializedHeaders));
                msg.Append(new ZFrame(message));

                // Reconnect if enpoint changed since the last message was sent.
                //if (_senderEndpoint != endPoint)
                //{
                    _senderEndpoint = endPoint;
                    _senderContext = new ZContext();
                    _sender = new ZSocket(_senderContext, ZSocketType.REQ);
                    
                    _sender.Connect(endPoint);
                //}

                // Send
                var poll = ZPollItem.CreateReceiver();
                var timeout = TimeSpan.FromMilliseconds(1);
                ZError error;
                int retriesLeft = 3;

                while (retriesLeft > 0)
                {
                    // Outgoing
                    if (!_sender.SendMessage(msg, out error))
                    {
                        if (error == ZError.ETERM)
                            return;    // Interrupted
                        throw new ZException(error);
                    }

                    // Incoming
                    ZMessage incoming;
                    while (true)
                    {
                        if (_sender.PollIn(poll, out incoming, out error, timeout))
                        {
                            using (incoming)
                            {
                                retriesLeft = 0;
                                break;
                            }
                        }
                        else
                        {
                            if (error == ZError.EAGAIN)
                            {
                                if (--retriesLeft == 0)
                                {
                                    Logger.Error("Server seems to be offline, abandoning");
                                    break;
                                }

                                Logger.Warn("No response from server, retrying…");

                                // Old socket is confused; close it and open a new one
                                _sender.Dispose();
                                _sender = new ZSocket(_senderContext, ZSocketType.REQ);
                                _sender.Connect(endPoint);

                                Logger.Info("I: reconnected");

                                // Send request again, on new socket
                                if (!_sender.SendMessage(msg, out error))
                                {
                                    if (error == ZError.ETERM)
                                        return;    // Interrupted
                                    throw new ZException(error);
                                }
 
                                continue;
                            }

                            if (error == ZError.ETERM)
                                return;    // Interrupted
                            throw new ZException(error);
                        }
                    }
                }

                _sender.Dispose();
                _senderContext.Dispose();
            }            
        }

        public void Disconnect()
        {
            Logger.Debug("In ZeroMQ.Producer.Disconnect()");

            this.Dispose();
        }

        public string Type { get; private set; }

        public long MaximumMessageSize { get; private set; }

        public void SendBytes(string endPoint, byte[] packet, Dictionary<string, string> headers)
        {
            throw new NotImplementedException();
        }

        private Dictionary<string, object> GetHeaders(Type type, Dictionary<string, string> headers, string queueName, string messageType)
        {
            if (headers == null)
            {
                headers = new Dictionary<string, string>();
            }

            if (!headers.ContainsKey("DestinationAddress"))
            {
                headers["DestinationAddress"] = queueName;
            }

            if (!headers.ContainsKey("MessageId"))
            {
                headers["MessageId"] = Guid.NewGuid().ToString();
            }

            if (!headers.ContainsKey("MessageType"))
            {
                headers["MessageType"] = messageType;
            }

            headers["SourceAddress"] = _transportSettings.QueueName;
            headers["TimeSent"] = DateTime.UtcNow.ToString("O");
            headers["SourceMachine"] = _transportSettings.MachineName;
            headers["FullTypeName"] = type.AssemblyQualifiedName;
            headers["TypeName"] = type.FullName;
            headers["ConsumerType"] = "ZeroMQ";
            headers["Language"] = "C#";

            return headers.ToDictionary(x => x.Key, x => (object)x.Value);
        }
    }
}
