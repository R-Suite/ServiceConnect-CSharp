using System;
using System.Collections.Generic;
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
        private IModel _model;
        private IConnection _connection;
        private readonly Object _lock = new Object();
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly ConnectionFactory _connectionFactory;
        private string[] _hosts;
        private int _activeHost;

        public Producer(ITransportSettings transportSettings, IDictionary<string, string> queueMappings, IMessageSerializer messageSerializer)
        {
            _transportSettings = transportSettings;
            _queueMappings = queueMappings;
            _messageSerializer = messageSerializer;

            _hosts = transportSettings.Host.Split(',');
            _activeHost = 0;

            _connectionFactory = new ConnectionFactory
            {
                HostName = _hosts[_activeHost],
                VirtualHost = "/",
                Protocol = Protocols.FromEnvironment(),
                Port = AmqpTcpEndpoint.UseDefaultPort
            };

            if (!string.IsNullOrEmpty(transportSettings.Username))
            {
                _connectionFactory.UserName = transportSettings.Username;
            }

            if (!string.IsNullOrEmpty(transportSettings.Password))
            {
                _connectionFactory.Password = transportSettings.Password;
            }

            CreateConnection();
        }

        private void CreateConnection()
        {
            _connection = _connectionFactory.CreateConnection();
            _model = _connection.CreateModel();
        }

        public void Publish<T>(T message, Dictionary<string, string> headers = null) where T : Message
        {
            DoPublish(message, headers);
        }

        private void PublishBaseType<T, TB>(T message, Dictionary<string, string> headers = null) where T : Message where TB : Message
        {
            DoPublish(message, headers, typeof(TB));
        }

        private void DoPublish<T>(T message, Dictionary<string, string> headers, Type baseType = null) where T : Message
        {
            var serializedMessage = _messageSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(serializedMessage);

            lock (_lock)
            {
                IBasicProperties basicProperties = _model.CreateBasicProperties();

                basicProperties.Headers = GetHeaders(typeof (T), headers, _transportSettings.Queue.Name, "Publish");
                basicProperties.MessageId = basicProperties.Headers["MessageId"].ToString(); // keep track of retries

                basicProperties.SetPersistent(true);
                var exchangeName = (null != baseType)
                    ? ConfigureExchange(baseType.FullName.Replace(".", string.Empty))
                    : ConfigureExchange(typeof (T).FullName.Replace(".", string.Empty));

                Retry.Do(() => _model.BasicPublish(exchangeName, _transportSettings.Queue.Name, basicProperties, bytes),
                    ex => RetryConnection(),
                    new TimeSpan(0, 0, 0, 6), 10);
            }

            // Get message BaseType and call Publish recursively
            Type newBaseType = (null != baseType) ? baseType.BaseType : typeof(T).BaseType;
            if (newBaseType != null && newBaseType.Name != typeof(Message).Name)
            {
                MethodInfo publish = GetType().GetMethod("PublishBaseType", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo genericPublish = publish.MakeGenericMethod(typeof(T), newBaseType);
                genericPublish.Invoke(this, new object[] {message, headers});
            }
        }

        private void RetryConnection()
        {
            if (_hosts.Length > 1)
            {
                if (_activeHost < _hosts.Length - 1)
                {
                    _activeHost++;
                }
                else
                {
                    _activeHost = 0;
                }
            }
            CreateConnection();
        }

        public void Send<T>(T message, Dictionary<string, string> headers = null) where T : Message
        {
            var serializedMessage = _messageSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(serializedMessage);

            lock (_lock)
            {
                IBasicProperties basicProperties = _model.CreateBasicProperties();

                basicProperties.SetPersistent(true);
                var endPoint = _queueMappings[typeof(T).FullName];

                basicProperties.Headers = GetHeaders(typeof(T), headers, endPoint, "Send");
                basicProperties.MessageId = basicProperties.Headers["MessageId"].ToString(); // keep track of retries

                Retry.Do(() => _model.BasicPublish(string.Empty, endPoint, basicProperties, bytes),
                         ex => RetryConnection(),
                         new TimeSpan(0, 0, 0, 6), 10);
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
                basicProperties.MessageId = basicProperties.Headers["MessageId"].ToString(); // keep track of retries

                Retry.Do(() => _model.BasicPublish(string.Empty, endPoint, basicProperties, bytes),
                         ex => RetryConnection(),
                         new TimeSpan(0, 0, 0, 6), 10);
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

            if (!headers.ContainsKey("MessageId"))
            {
                headers["MessageId"] = Guid.NewGuid().ToString();
            }

            headers["SourceAddress"] = _transportSettings.Queue.Name;
            headers["TimeSent"] = DateTime.UtcNow.ToString("O");
            headers["SourceMachine"] = _transportSettings.MachineName;
            headers["MessageType"] = messageType;
            headers["FullTypeName"] = type.AssemblyQualifiedName;
            headers["TypeName"] = type.Name;

            return headers.ToDictionary(x => x.Key, x => (object)x.Value);
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