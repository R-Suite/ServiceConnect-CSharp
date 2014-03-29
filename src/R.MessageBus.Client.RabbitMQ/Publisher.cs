using System;
using System.Text;
using R.MessageBus.Interfaces;
using RabbitMQ.Client;

namespace R.MessageBus.Client.RabbitMQ
{
    public class Publisher : IDisposable, IPublisher
    {
        private readonly ITransportSettings _transportSettings;
        private readonly IModel _model;
        private readonly IConnection _connection;
        private readonly string _exchange;
        private readonly IJsonMessageSerializer _serializer = new JsonMessageSerializer();

        public Publisher(ITransportSettings transportSettings)
        {
            _transportSettings = transportSettings;
            var connectionFactory = new ConnectionFactory 
            {
                HostName = _transportSettings.Host,
                VirtualHost = "/",
                Protocol = Protocols.FromEnvironment(),
                Port = AmqpTcpEndpoint.UseDefaultPort
            };

            if (!string.IsNullOrEmpty(_transportSettings.Username))
            {
                connectionFactory.UserName = _transportSettings.Username;
            }

            if (!string.IsNullOrEmpty(_transportSettings.Password))
            {
                connectionFactory.Password = _transportSettings.Password;
            }

            _connection = connectionFactory.CreateConnection();
            _model = _connection.CreateModel();
            _exchange = ConfigureExchange();
        }

        public void Publish<T>(T message) where T : Message
        {
            var messageJson = _serializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(messageJson);
            IBasicProperties basicProperties = _model.CreateBasicProperties();
            basicProperties.MessageId = Guid.NewGuid().ToString(); // keep track of retries
            basicProperties.SetPersistent(true);
            _model.BasicPublish(_exchange, typeof(T).FullName.Replace(".", string.Empty), basicProperties, bytes);
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

        private string ConfigureExchange()
        {
            var arguments = _transportSettings.Exchange.Arguments;

            _model.ExchangeDeclare(_transportSettings.Exchange.Name,
                                    "fanout",
                                    _transportSettings.Exchange.Durable,
                                    _transportSettings.Exchange.AutoDelete,
                                    arguments);

            return _transportSettings.Exchange.Name;
        }
    }
}