using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using R.MessageBus.Interfaces;
using RabbitMQ.Client;

namespace R.MessageBus.Client.RabbitMQ
{
    public class Producer : IDisposable, IProducer
    {
        private readonly ITransportSettings _transportSettings;
        private readonly IDictionary<string, string> _queueMappings;
        private readonly IMessageSerializer _messageSerializer;
        private readonly IModel _model;
        private readonly IConnection _connection;

        public Producer(ITransportSettings transportSettings, IDictionary<string, string> queueMappings, IMessageSerializer messageSerializer)
        {
            _transportSettings = transportSettings;
            _queueMappings = queueMappings;
            _messageSerializer = messageSerializer;

            var connectionFactory = new ConnectionFactory 
            {
                HostName = transportSettings.Host,
                VirtualHost = "/",
                Protocol = Protocols.FromEnvironment(),
                Port = AmqpTcpEndpoint.UseDefaultPort
            };

            if (!string.IsNullOrEmpty(transportSettings.Username))
            {
                connectionFactory.UserName = transportSettings.Username;
            }

            if (!string.IsNullOrEmpty(transportSettings.Password))
            {
                connectionFactory.Password = transportSettings.Password;
            }

            _connection = connectionFactory.CreateConnection();
            _model = _connection.CreateModel();
        }

        public void Publish<T>(T message, Dictionary<string, string> headers = null) where T : Message
        {
            var serializedMessage = _messageSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(serializedMessage);
            IBasicProperties basicProperties = _model.CreateBasicProperties();
            basicProperties.MessageId = Guid.NewGuid().ToString(); // keep track of retries

            basicProperties.Headers = GetHeaders(headers, _transportSettings.Queue.Name);

            basicProperties.SetPersistent(true);
            var exchangeName = ConfigureExchange(typeof(T).FullName.Replace(".", string.Empty));
            _model.BasicPublish(exchangeName, _transportSettings.Queue.Name, basicProperties, bytes); // (use endpoint as routing key (in retries))
        }

        public void Send<T>(T message, Dictionary<string, string> headers = null) where T : Message
        {
            var serializedMessage = _messageSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(serializedMessage);
            IBasicProperties basicProperties = _model.CreateBasicProperties();
            basicProperties.MessageId = Guid.NewGuid().ToString(); // keep track of retries

            basicProperties.SetPersistent(true);
            var endPoint = _queueMappings[typeof(T).FullName];

            basicProperties.Headers = GetHeaders(headers, endPoint);

            _model.BasicPublish(string.Empty, endPoint, basicProperties, bytes);
        }

        public void Send<T>(string endPoint, T message, Dictionary<string, string> headers = null) where T : Message
        {
            var serializedMessage = _messageSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(serializedMessage);
            IBasicProperties basicProperties = _model.CreateBasicProperties();
            basicProperties.SetPersistent(true);

            basicProperties.Headers = GetHeaders(headers, endPoint);

            //TimeSpan t = DateTime.UtcNow - new DateTime(1970, 1, 1);
            //var secondsSinceEpoch = (long)t.TotalSeconds;
            //basicProperties.Timestamp = new AmqpTimestamp(secondsSinceEpoch);

            basicProperties.MessageId = Guid.NewGuid().ToString(); // keep track of retries
            _model.BasicPublish(string.Empty, endPoint, basicProperties, bytes);
        }

        private Dictionary<string, object> GetHeaders(Dictionary<string, string> headers, string queueName)
        {
            if (headers == null)
            {
                headers = new Dictionary<string, string>();
            }

            if (!headers.ContainsKey("SourceAddress"))
            {
                headers["SourceAddress"] = queueName;
            }

            return headers.ToDictionary(x => x.Key, x => (object) x.Value);
        }

        public void Disconnect()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_connection != null)
                _connection.Close();
            if (_model != null)
                _model.Abort();
        }

        private string ConfigureExchange(string exchangeName)
        {
            _model.ExchangeDeclare(exchangeName, "fanout", true);

            return exchangeName;
        }
    }
}