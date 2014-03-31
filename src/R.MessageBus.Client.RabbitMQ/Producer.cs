using System;
using System.Collections.Generic;
using System.Text;
using R.MessageBus.Interfaces;
using RabbitMQ.Client;

namespace R.MessageBus.Client.RabbitMQ
{
    public class Producer : IDisposable, IProducer
    {
        private readonly ITransportSettings _transportSettings;
        private readonly IDictionary<string, string> _endPointMappings;
        private readonly IModel _model;
        private readonly IConnection _connection;
        private readonly IJsonMessageSerializer _serializer = new JsonMessageSerializer();

        public Producer(ITransportSettings transportSettings, IDictionary<string, string> endPointMappings)
        {
            _transportSettings = transportSettings;
            _endPointMappings = endPointMappings;

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

        public void Publish<T>(T message) where T : Message
        {
            var messageJson = _serializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(messageJson);
            IBasicProperties basicProperties = _model.CreateBasicProperties();
            basicProperties.MessageId = Guid.NewGuid().ToString(); // keep track of retries
            basicProperties.SetPersistent(true);
            var exchangeName = ConfigureExchange(typeof(T).FullName.Replace(".", string.Empty));
            _model.BasicPublish(exchangeName, _transportSettings.Queue.Name, basicProperties, bytes); // (use endpoint as routing key (in retries))
        }

        public void Send<T>(T message) where T : Message
        {
            var messageJson = _serializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(messageJson);
            IBasicProperties basicProperties = _model.CreateBasicProperties();
            basicProperties.MessageId = Guid.NewGuid().ToString(); // keep track of retries
            basicProperties.SetPersistent(true);
            var endPoint = _endPointMappings[typeof (T).FullName];
            _model.BasicPublish(string.Empty, endPoint, basicProperties, bytes);
        }

        public void Send<T>(string endPoint, T message) where T : Message
        {
            var messageJson = _serializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(messageJson);
            IBasicProperties basicProperties = _model.CreateBasicProperties();
            basicProperties.MessageId = Guid.NewGuid().ToString(); // keep track of retries
            basicProperties.SetPersistent(true);
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