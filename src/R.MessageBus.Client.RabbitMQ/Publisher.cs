using System;
using System.Text;
using R.MessageBus.Interfaces;
using RabbitMQ.Client;

namespace R.MessageBus.Client.RabbitMQ
{
    public class Publisher : IDisposable, IPublisher
    {
        private readonly IModel _model;
        private readonly IConnection _connection;
        private readonly IJsonMessageSerializer _serializer = new JsonMessageSerializer();

        public Publisher(ITransportSettings transportSettings)
        {
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
            _model.BasicPublish(exchangeName, string.Empty, basicProperties, bytes);
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