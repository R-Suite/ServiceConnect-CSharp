using System;
using System.Collections.Generic;
using System.Configuration;
using R.MessageBus.Interfaces;
using RabbitMQ.Client;

namespace R.MessageBus.Client.RabbitMQ
{
    public class Publisher : IDisposable, IPublisher
    {
        private readonly IModel _model;
        private readonly IConnection _connection;
        private readonly string _exchange;
        private readonly Settings.Settings _settings;

        public Publisher(string configPath = null, string endPoint = null)
        {
            var configurationManager = new ConfigurationManagerWrapper(configPath);

            var section = configurationManager.GetSection<BusSettings.BusSettings>("BusSettings");

            if (section == null)
            {
                throw new ConfigurationErrorsException("The configuration file must contain a BusSettings section");
            }

            Settings.Settings settings = section.Settings.GetItemByKey(endPoint);

            if (settings == null)
            {
                throw new ConfigurationErrorsException(string.Format("Settings for endpoint {0} could not be found", endPoint));
            }

            _settings = settings;

            var connectionFactory = new ConnectionFactory 
            { 
                HostName = _settings.Host,
                VirtualHost = "/",
                Protocol = Protocols.FromEnvironment(),
                Port = AmqpTcpEndpoint.UseDefaultPort
            };

            if (!string.IsNullOrEmpty(_settings.Username))
            {
                connectionFactory.UserName = _settings.Username;
            }

            if (!string.IsNullOrEmpty(_settings.Password))
            {
                connectionFactory.Password = _settings.Password;
            }

            _connection = connectionFactory.CreateConnection();
            _model = _connection.CreateModel();
            _exchange = ConfigureExchange();
        }

        public void Publish<T>(T message) where T : Message
        {
            var bytes = message.ToByteArray();
            IBasicProperties basicProperties = _model.CreateBasicProperties();
            basicProperties.MessageId = Guid.NewGuid().ToString(); // used to keep track of retries
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
            if (!string.IsNullOrEmpty(_settings.Exchange.Name))
            {
                var arguments = new Dictionary<string, object>();
                if (_settings.Exchange.Arguments != null)
                {
                    for (int i = 0; i < _settings.Exchange.Arguments.Count; i++)
                    {
                        arguments.Add(_settings.Exchange.Arguments[i].Name, _settings.Exchange.Arguments[i].Value);
                    }
                }

                _model.ExchangeDeclare(_settings.Exchange.Name,
                                       _settings.Exchange.Type,
                                       _settings.Exchange.Durable,
                                       _settings.Exchange.AutoDelete,
                                       arguments);
            }
            return _settings.Exchange.Name;
        }
    }
}