using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ServiceConnect.Interfaces;
using Confluent.Kafka;
using Newtonsoft.Json;

namespace ServiceConnect.Client.Kafka
{
    public class Producer : IProducer
    {
        private readonly ITransportSettings _transportSettings;
        private readonly IDictionary<string, IList<string>> _consumerGroupMappings;
        private readonly ILogger _logger;       
        private readonly long _maxMessageSize;
        private readonly ProducerConfig _conf;
        private readonly ProducerBuilder<Null, string> _producer;

        private readonly Action<DeliveryReport<Null, string>> _handler = r => {
            if (r.Error.IsError) {
                _logger.Error($"Delivery Error: {r.Error.Reason}");
            }
        };

        public Producer(ITransportSettings transportSettings, IDictionary<string, IList<string>> consumerGroupMappings, ILogger logger)
        {
            _transportSettings = transportSettings;
            _consumerGroupMappings = consumerGroupMappings;
            _logger = logger;
            _maxMessageSize = transportSettings.ClientSettings.ContainsKey("MessageSize") ? Convert.ToInt64(_transportSettings.ClientSettings["MessageSize"]) : 65536;
            
            _conf = new ProducerConfig { BootstrapServers = transportSettings.Host };
            _producer = new ProducerBuilder<Null, string>(conf).Build();

            // wait for up to 10 seconds for any inflight messages to be delivered.
            _producer.Flush(TimeSpan.FromSeconds(10));            
        }        
        public void Publish(Type type, byte[] message, Dictionary<string, string> headers = null)
        {
            Dictionary<string, string> messageHeaders = GetHeaders(type, headers, string.Empty, "Publish");
            var msg = new Dictionary<string, object> {
                { headers, messageHeaders},
                { message, message }
            };
            var messageString = JsonConvert.SerializeObject(msg);
            p.Produce(endPoint, new Message<Null, string> { Value = messageString }, _handler);
        }

        public void Send(Type type, byte[] message, Dictionary<string, string> headers = null)
        {
            IList<string> endPoints = _consumerGroupMappings[type.FullName];
            foreach (string endPoint in endPoints)
            {
                Send(endPoint, type, message, headers);
            }
        }

      
        public void Send(string endPoint, Type type, byte[] message, Dictionary<string, string> headers = null)
        {
            Dictionary<string, string> messageHeaders = GetHeaders(type, headers, endPoint, "Send");
            var msg = new Dictionary<string, object> {
                { headers, messageHeaders},
                { message, message }
            };
            var messageString = JsonConvert.SerializeObject(msg);
            p.Produce(type.FullName, new Message<Null, string> { Value = messageString }, _handler);
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
            headers["TypeName"] = type.FullName;
            headers["FullTypeName"] = type.AssemblyQualifiedName;
            headers["ConsumerType"] = "Kafka";
            headers["Language"] = "C#";

            return headers.ToDictionary(x => x.Key, x => (object)x.Value);
        }

        public void Disconnect()
        {
        }

        public void Dispose()
        {
            //_producer();
        }

        public string Type
        {
            get
            {
                return "Kafka";
            }
        }

        public long MaximumMessageSize
        {
            get { return _maxMessageSize; }
        }

        public void SendBytes(string endPoint, byte[] packet, Dictionary<string, string> headers)
        {
            
        }       
    }

}