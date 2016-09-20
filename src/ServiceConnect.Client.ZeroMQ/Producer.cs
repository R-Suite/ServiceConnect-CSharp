using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Common.Logging;
using NetMQ.Sockets;
using Newtonsoft.Json;
using ServiceConnect.Interfaces;
using NetMQ;

namespace ServiceConnect.Client.ZeroMQ
{
    /// <summary>
    /// ***************
    /// Experimental - NOT for production use
    /// ***************
    /// </summary>
    public class Producer : IProducer
    {
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly ITransportSettings _transportSettings;
        private readonly IDictionary<string, IList<string>> _queueMappings;
        private readonly Object _lock = new Object();
        private const int RequestTimeout = 2500;
        private const int RequestRetries = 3;
        private RequestSocket _sender;
        private static bool _expectReply = true;
        private static int _retriesLeft = RequestRetries;

        public Producer(ITransportSettings transportSettings, IDictionary<string, IList<string>> queueMappings)
        {
            _transportSettings = transportSettings;
            _queueMappings = queueMappings;

            if (_transportSettings.ClientSettings.ContainsKey("PublisherHost"))
            {
                //_publishContext = new ZContext();
                //_publisher = new ZSocket(_publishContext, ZSocketType.PUB);
                //_publisher.Linger = TimeSpan.FromMilliseconds(1);
                //_publisher.Bind(_transportSettings.ClientSettings["PublisherHost"].ToString());
            }
        }

        public void Dispose()
        {
            if (null != _sender)
            {
                _sender.Dispose();
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

            throw new NotImplementedException();
        }

        public void Send(Type type, byte[] message, Dictionary<string, string> headers = null)
        {
            IList<string> endPoints = _queueMappings[type.FullName];

            foreach (string endPoint in endPoints)
            {
                Send(endPoint, type, message, headers);
            }
        }

        public void Send(string endPoint, Type type, byte[] message, Dictionary<string, string> headers = null)
        {
            lock (_lock)
            {
                Dictionary<string, object> messageHeaders = GetHeaders(type, headers, endPoint, "Send");
                var serializedHeaders = JsonConvert.SerializeObject(messageHeaders);

                var msg = new NetMQMessage();
                msg.Append(new NetMQFrame(serializedHeaders));
                msg.Append(new NetMQFrame(message));

                _sender = CreateServerSocket(endPoint);
                _sender.SendMultipartMessage(msg);
                _expectReply = true;

                while (_expectReply)
                {
                    bool result = _sender.Poll(TimeSpan.FromMilliseconds(RequestTimeout));

                    if (result)
                        continue;

                    _retriesLeft--;

                    if (_retriesLeft == 0)
                    {
                        Logger.Error("Sender: Server seems to be offline, abandoning");
                        break;
                    }

                    Logger.Warn("Sender: No response from server, retrying...");

                    TerminateClient(_sender, endPoint);

                    _sender = CreateServerSocket(endPoint);
                    _sender.SendMultipartMessage(msg);
                }

                TerminateClient(_sender, endPoint);
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

        private static void TerminateClient(NetMQSocket client, string serverEndpoint)
        {
            client.Disconnect(serverEndpoint);
            client.Close();
        }

        private static RequestSocket CreateServerSocket(string serverEndpoint)
        {
            Logger.Debug("Sender: Connecting to server...");

            var client = new RequestSocket();
            client.Connect(serverEndpoint);
            client.Options.Linger = TimeSpan.Zero;
            client.ReceiveReady += ClientOnReceiveReady;

            return client;
        }

        private static void ClientOnReceiveReady(object sender, NetMQSocketEventArgs args)
        {
            var strReply = args.Socket.ReceiveFrameString();

            if (strReply == "ok")
            {
                Logger.DebugFormat("Sender: Server replied OK ({0})", strReply);
                _retriesLeft = RequestRetries;
                _expectReply = false;
            }
            else
            {
                Logger.ErrorFormat("Sender: Malformed reply from server: {0}", strReply);
            }
        }
    }
}
