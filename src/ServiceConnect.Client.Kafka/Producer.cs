using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly IProducer<Null, string> _producer;
        private readonly IProducer<Null, byte[]> _bytesProducer;

        private void ProducerHandler(DeliveryReport<Null, string> report)
        {
            if (report.Error.IsError)
            {
                _logger.Error($"Delivery Error: {report.Error.Reason}");
            }
        }

        public Producer(ITransportSettings transportSettings, IDictionary<string, IList<string>> consumerGroupMappings, ILogger logger)
        {
            _transportSettings = transportSettings;
            _consumerGroupMappings = consumerGroupMappings;
            _logger = logger;
            _maxMessageSize = transportSettings.ClientSettings.ContainsKey("MessageSize") ? Convert.ToInt64(_transportSettings.ClientSettings["MessageSize"]) : 65536;
            
            _conf = new ProducerConfig { BootstrapServers = transportSettings.Host };
            _producer = new ProducerBuilder<Null, string>(_conf).Build();
            _bytesProducer = new ProducerBuilder<Null, byte[]>(_conf).Build();

            // wait for up to 10 seconds for any inflight messages to be delivered.
            _producer.Flush(TimeSpan.FromSeconds(10));
            _bytesProducer.Flush(TimeSpan.FromSeconds(10));
        }        
        public void Publish(Type type, byte[] message, Dictionary<string, string> headers = null)
        {
            Dictionary<string, object> messageHeaders = GetHeaders(type, headers, string.Empty, "Publish");
            var msg = new MessageWrapper
            {
                Headers = messageHeaders,
                Message = message
            };
            var messageString = JsonConvert.SerializeObject(msg);
            _producer.Produce(type.FullName, new Message<Null, string> { Value = messageString }, ProducerHandler);
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
            Dictionary<string, object> messageHeaders = GetHeaders(type, headers, endPoint, "Send");
            var msg = new MessageWrapper
            {
                Headers = messageHeaders,
                Message = message
            };
            var messageString = JsonConvert.SerializeObject(msg);
            _producer.Produce(endPoint, new Message<Null, string> { Value = messageString }, ProducerHandler);
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
            _producer.Dispose();
            _bytesProducer.Dispose();
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