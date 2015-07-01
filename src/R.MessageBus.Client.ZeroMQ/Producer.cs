using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using R.MessageBus.Interfaces;
using ZeroMQ;

namespace R.MessageBus.Client.ZeroMQ
{
    public class Producer : IProducer
    {
        private readonly ITransportSettings _transportSettings;

        public Producer(ITransportSettings transportSettings, IDictionary<string, IList<string>> queueMappings)
        {
            _transportSettings = transportSettings;
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }

        public void Publish(string type, byte[] message, Dictionary<string, string> headers = null)
        {
            throw new NotImplementedException();
        }

        public void Send(string type, byte[] message, Dictionary<string, string> headers = null)
        {
            throw new NotImplementedException();
        }

        public void Send(string endPoint, string type, byte[] message, Dictionary<string, string> headers = null)
        {
            Dictionary<string, object> messageHeaders = GetHeaders(type, headers, endPoint, "Send");
            var serializedHeaders = JsonConvert.SerializeObject(messageHeaders);

            using (var context = new ZContext())
            using (var requester = new ZSocket(context, ZSocketType.PUSH))
            {
                // Connect
                requester.Connect(_transportSettings.Host);

                var msg = new ZMessage();
                msg.Append(new ZFrame(serializedHeaders));
                msg.Append(new ZFrame(message));

                // Send
                requester.SendMessage(msg);
            }
        }

        public void Disconnect()
        {
            throw new NotImplementedException();
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
