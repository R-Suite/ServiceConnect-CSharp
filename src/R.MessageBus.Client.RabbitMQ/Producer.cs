using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using log4net;
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
        private readonly Object _lock = new Object();
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

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

            lock (_lock)
            {
                IBasicProperties basicProperties = _model.CreateBasicProperties();
                basicProperties.MessageId = Guid.NewGuid().ToString(); // keep track of retries

                basicProperties.Headers = GetHeaders(typeof(T), headers, _transportSettings.Queue.Name, "Publish");

                basicProperties.SetPersistent(true);
                var exchangeName = ConfigureExchange(typeof (T).FullName.Replace(".", string.Empty));
                _model.BasicPublish(exchangeName, _transportSettings.Queue.Name, basicProperties, bytes);
            }
        }

        public void Send<T>(T message, Dictionary<string, string> headers = null) where T : Message
        {
            var serializedMessage = _messageSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(serializedMessage);

            lock (_lock)
            {
                IBasicProperties basicProperties = _model.CreateBasicProperties();
                basicProperties.MessageId = Guid.NewGuid().ToString(); // keep track of retries

                basicProperties.SetPersistent(true);
                var endPoint = _queueMappings[typeof (T).FullName];

                basicProperties.Headers = GetHeaders(typeof(T), headers, endPoint, "Send");

                _model.BasicPublish(string.Empty, endPoint, basicProperties, bytes);
            }
        }

        public void Send<T>(string endPoint, T message, Dictionary<string, string> headers = null) where T : Message
        {
            var serializedMessage = _messageSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(serializedMessage);

            lock (_lock)
            {
                IBasicProperties basicProperties = _model.CreateBasicProperties();
                basicProperties.SetPersistent(true);

                basicProperties.Headers = GetHeaders(typeof(T), headers, endPoint, "Send");

                basicProperties.MessageId = Guid.NewGuid().ToString(); // keep track of retries
                _model.BasicPublish(string.Empty, endPoint, basicProperties, bytes);
            }
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

            headers["SourceAddress"] = _transportSettings.Queue.Name;
            headers["TimeSent"] = DateTime.UtcNow.ToString("O");
            headers["SourceMachine"] = _transportSettings.MachineName;
            headers["MessageType"] = messageType;
            headers["FullTypeName"] = type.FullName;
            headers["TypeName"] = type.Name;

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
            try
            {
                _model.ExchangeDeclare(exchangeName, "fanout", true);
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring exchange - {0}", ex.Message));
            }

            return exchangeName;
        }
    }
}