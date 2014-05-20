using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Text;
using R.MessageBus.Interfaces;
using RabbitMQ.Client;

namespace R.MessageBus.Client.RabbitMQ
{
    public class Producer : IDisposable, IProducer
    {
        private readonly ITransportSettings _transportSettings;
        private readonly IDictionary<string, string> _queueMappings;
        private readonly IModel _model;
        private readonly IConnection _connection;
        private readonly IJsonMessageSerializer _serializer = new JsonMessageSerializer();

        public Producer(ITransportSettings transportSettings, IDictionary<string, string> queueMappings)
        {
            _transportSettings = transportSettings;
            _queueMappings = queueMappings;

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
            var messageJson = _serializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(messageJson);
            IBasicProperties basicProperties = _model.CreateBasicProperties();
            basicProperties.MessageId = Guid.NewGuid().ToString(); // keep track of retries

            if (null != headers)
            {
                basicProperties.Headers = headers.ToDictionary(x => x.Key, x => (object)x.Value); ;
            }

            basicProperties.SetPersistent(true);
            var exchangeName = ConfigureExchange(typeof(T).FullName.Replace(".", string.Empty));
            _model.BasicPublish(exchangeName, _transportSettings.Queue.Name, basicProperties, bytes); // (use endpoint as routing key (in retries))
        }

        public void Send<T>(T message, Dictionary<string, string> headers = null) where T : Message
        {
            var messageJson = _serializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(messageJson);
            IBasicProperties basicProperties = _model.CreateBasicProperties();
            basicProperties.MessageId = Guid.NewGuid().ToString(); // keep track of retries
            
            if (null != headers)
            {
                basicProperties.Headers = headers.ToDictionary(x => x.Key, x => (object)x.Value); ;
            }

            basicProperties.SetPersistent(true);
            var endPoint = _queueMappings[typeof(T).FullName];
            _model.BasicPublish(string.Empty, endPoint, basicProperties, bytes);
        }

        public void Send<T>(string endPoint, T message, Dictionary<string, string> headers = null) where T : Message
        {
            var messageJson = _serializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(messageJson);
            IBasicProperties basicProperties = _model.CreateBasicProperties();
            basicProperties.SetPersistent(true);
            basicProperties.ReplyTo = _transportSettings.Queue.Name;

            //TimeSpan t = DateTime.UtcNow - new DateTime(1970, 1, 1);
            //var secondsSinceEpoch = (long)t.TotalSeconds;
            //basicProperties.Timestamp = new AmqpTimestamp(secondsSinceEpoch);

            if (null != headers)
            {
                basicProperties.Headers = headers.ToDictionary(x => x.Key, x => (object)x.Value);
            }

            basicProperties.MessageId = Guid.NewGuid().ToString(); // keep track of retries
            _model.BasicPublish(string.Empty, endPoint, basicProperties, bytes);
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