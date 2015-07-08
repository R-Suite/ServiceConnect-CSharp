using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Common.Logging;
using Newtonsoft.Json;
using R.MessageBus.Interfaces;
using ZeroMQ;

namespace R.MessageBus.Client.ZeroMQ
{
    public class Producer : IProducer
    {
        private readonly ITransportSettings _transportSettings;
        private readonly IDictionary<string, IList<string>> _queueMappings;
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly ZSocket _publisher;
        private readonly ZContext _publishContext;
        private readonly ZSocket _sender;
        private readonly ZContext _senderContext;

        public Producer(ITransportSettings transportSettings, IDictionary<string, IList<string>> queueMappings)
        {
            _transportSettings = transportSettings;
            _queueMappings = queueMappings;

            if (_transportSettings.ClientSettings.ContainsKey("PublisherHost"))
            {
                _publishContext = new ZContext();
                _publisher = new ZSocket(_publishContext, ZSocketType.PUB);
                _publisher.Linger = TimeSpan.Zero;
                _publisher.Bind(_transportSettings.ClientSettings["PublisherHost"].ToString());
            }

            if (_transportSettings.ClientSettings.ContainsKey("SenderHost"))
            {
                _senderContext = new ZContext();
                _sender = new ZSocket(_senderContext, ZSocketType.PAIR);
                _sender.Bind(_transportSettings.ClientSettings["SenderHost"].ToString());
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

        public void Publish(string type, byte[] message, Dictionary<string, string> headers = null)
        {
            Dictionary<string, object> messageHeaders = GetHeaders(type, headers, _transportSettings.QueueName, "Publish");
            var serializedHeaders = JsonConvert.SerializeObject(messageHeaders);

            var msg = new ZMessage();
            msg.Append(new ZFrame(type.Replace(".", string.Empty)));
            msg.Append(new ZFrame(serializedHeaders));
            msg.Append(new ZFrame(message));

            _publisher.SendMessage(msg);
        }

        public void Send(string type, byte[] message, Dictionary<string, string> headers = null)
        {
            IList<string> endPoints = _queueMappings[type];

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

        public void Send(string endPoint, string type, byte[] message, Dictionary<string, string> headers = null)
        {
            Dictionary<string, object> messageHeaders = GetHeaders(type, headers, endPoint, "Send");
            var serializedHeaders = JsonConvert.SerializeObject(messageHeaders);

            var msg = new ZMessage();
            msg.Append(new ZFrame(serializedHeaders));
            msg.Append(new ZFrame(message));

            // Send
            _sender.SendMessage(msg);
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

        private Dictionary<string, object> GetHeaders(string type, Dictionary<string, string> headers, string queueName, string messageType)
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
            headers["FullTypeName"] = type;
            headers["ConsumerType"] = "ZeroMQ";
            headers["Language"] = "C#";

            return headers.ToDictionary(x => x.Key, x => (object)x.Value);
        }
    }
}
