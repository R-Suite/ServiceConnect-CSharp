//Copyright (C) 2015  Timothy Watson, Jakub Pachansky

//This program is free software; you can redistribute it and/or
//modify it under the terms of the GNU General Public License
//as published by the Free Software Foundation; either version 2
//of the License, or (at your option) any later version.

//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//GNU General Public License for more details.

//You should have received a copy of the GNU General Public License
//along with this program; if not, write to the Free Software
//Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Common.Logging;
using Newtonsoft.Json;
using R.MessageBus.Interfaces;
using RabbitMQ.Client;

namespace R.MessageBus.Client.RabbitMQ
{
    public class Producer : IProducer
    {
        private readonly ITransportSettings _transportSettings;
        private readonly IDictionary<string, IList<string>> _queueMappings;
        private IModel _model;
        private IConnection _connection;
        private readonly Object _lock = new Object();
        private static readonly ILog Logger = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly ConnectionFactory _connectionFactory;
        private readonly string[] _hosts;
        private int _activeHost;
        private readonly long _maxMessageSize;

        public Producer(ITransportSettings transportSettings, IDictionary<string, IList<string>> queueMappings)
        {
            _transportSettings = transportSettings;
            _queueMappings = queueMappings;
            _maxMessageSize = transportSettings.ClientSettings.ContainsKey("MessageSize") ? Convert.ToInt64(_transportSettings.ClientSettings["MessageSize"]) : 65536;
            _hosts = transportSettings.Host.Split(',');
            _activeHost = 0;

            _connectionFactory = new ConnectionFactory
            {
                HostName = _hosts[_activeHost],
                VirtualHost = "/",
                Protocol = Protocols.DefaultProtocol,
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

            if (_transportSettings.SslEnabled)
            {
                _connectionFactory.Ssl = new SslOption
                {
                    Enabled = true,
                    AcceptablePolicyErrors = transportSettings.AcceptablePolicyErrors,
                    ServerName = transportSettings.ServerName,
                    CertPassphrase = transportSettings.CertPassphrase,
                    CertPath = transportSettings.CertPath,
                    Certs = transportSettings.Certs,
                    Version = transportSettings.Version,
                    CertificateSelectionCallback = transportSettings.CertificateSelectionCallback,
                    CertificateValidationCallback = transportSettings.CertificateValidationCallback
                };
                _connectionFactory.Port = AmqpTcpEndpoint.DefaultAmqpSslPort;
            }

            if (!string.IsNullOrEmpty(transportSettings.VirtualHost))
            {
                _connectionFactory.VirtualHost = transportSettings.VirtualHost;
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

        private void PublishBaseType<T, TB>(T message, Dictionary<string, string> headers = null)
            where T : Message
            where TB : Message
        {
            DoPublish(message, headers, typeof(TB));
        }

        private void DoPublish<T>(T message, Dictionary<string, string> headers, Type baseType = null) where T : Message
        {
            var serializedMessage = JsonConvert.SerializeObject(message);
            var bytes = Encoding.UTF8.GetBytes(serializedMessage);

            lock (_lock)
            {
                IBasicProperties basicProperties = _model.CreateBasicProperties();

                var messageHeaders = GetHeaders(typeof(T), headers, _transportSettings.QueueName, "Publish");

                var envelope = new Envelope
                {
                    Body = bytes,
                    Headers = messageHeaders
                };

                basicProperties.Headers = envelope.Headers;
                basicProperties.MessageId = basicProperties.Headers["MessageId"].ToString(); // keep track of retries
                
                basicProperties.SetPersistent(true);
                var exchangeName = (null != baseType)
                    ? ConfigureExchange(baseType.FullName.Replace(".", string.Empty))
                    : ConfigureExchange(typeof(T).FullName.Replace(".", string.Empty));

                Retry.Do(() => _model.BasicPublish(exchangeName, _transportSettings.QueueName, basicProperties, envelope.Body),
                    ex => RetryConnection(),
                    new TimeSpan(0, 0, 0, 6), 10);
            }

            // Get message BaseType and call Publish recursively
            Type newBaseType = (null != baseType) ? baseType.BaseType : typeof(T).BaseType;

            if (newBaseType != null && newBaseType.Name != typeof(Message).Name)
            {
                MethodInfo publish = GetType().GetMethod("PublishBaseType", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo genericPublish = publish.MakeGenericMethod(typeof(T), newBaseType);
                genericPublish.Invoke(this, new object[] { message, headers });
            }
        }

        private void RetryConnection()
        {
            Logger.Debug("In Producer.RetryConnection()");

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
            var serializedMessage = JsonConvert.SerializeObject(message);
            var bytes = Encoding.UTF8.GetBytes(serializedMessage);

            lock (_lock)
            {
                IBasicProperties basicProperties = _model.CreateBasicProperties();

                basicProperties.SetPersistent(true);
                IList<string> endPoints = _queueMappings[typeof(T).FullName];

               

                foreach (string endPoint in endPoints)
                {
                    var messageHeaders = GetHeaders(typeof(T), headers, endPoint, "Send");

                    var envelope = new Envelope
                    {
                        Body = bytes,
                        Headers = messageHeaders
                    };

                    basicProperties.Headers = envelope.Headers;
                    basicProperties.MessageId = basicProperties.Headers["MessageId"].ToString(); // keep track of retries

                    Retry.Do(() => _model.BasicPublish(string.Empty, endPoint, basicProperties, envelope.Body),
                             ex => RetryConnection(),
                             new TimeSpan(0, 0, 0, 6), 10);
                }
            }
        }

        public void Send<T>(string endPoint, T message, Dictionary<string, string> headers = null) where T : Message
        {
            var serializedMessage = JsonConvert.SerializeObject(message);
            var bytes = Encoding.UTF8.GetBytes(serializedMessage);

            lock (_lock)
            {
                IBasicProperties basicProperties = _model.CreateBasicProperties();
                basicProperties.SetPersistent(true);

                var messageHeaders = GetHeaders(typeof(T), headers, endPoint, "Send");

                var envelope = new Envelope
                {
                    Body = bytes,
                    Headers = messageHeaders
                };

                basicProperties.Headers = envelope.Headers; 
                basicProperties.MessageId = basicProperties.Headers["MessageId"].ToString(); // keep track of retries

                Retry.Do(() => _model.BasicPublish(string.Empty, endPoint, basicProperties, envelope.Body),
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

            if (!headers.ContainsKey("MessageType"))
            {
                headers["MessageType"] = messageType;
            }

            headers["SourceAddress"] = _transportSettings.QueueName;
            headers["TimeSent"] = DateTime.UtcNow.ToString("O");
            headers["SourceMachine"] = _transportSettings.MachineName;
            headers["FullTypeName"] = type.AssemblyQualifiedName;
            headers["TypeName"] = type.Name;
            headers["ConsumerType"] = "RabbitMQ";
            headers["Language"] = "C#";

            return headers.ToDictionary(x => x.Key, x => (object)x.Value);
        }

        public void Disconnect()
        {
            Logger.Debug("In Producer.Disconnect()");

            Dispose();
        }

        public void Dispose()
        {
            Logger.Debug("In Producer.Dispose()");

            if (_model != null)
            {
                Logger.Debug("Disposing Model");
                _model.Dispose();
                _model = null;
            }

            if (_connection != null)
            {
                try
                {
                    Logger.Debug("Disposing connection");
                    _connection.Dispose();
                }
                catch (System.IO.EndOfStreamException ex)
                {
                    Logger.Warn("Error disposing connection", ex);
                }
                _connection = null;
            }
        }

        public string Type
        {
            get
            {
                return "RabbitMQ";
            }
        }

        public long MaximumMessageSize
        {
            get { return _maxMessageSize; }
        }

        public void SendBytes(string endPoint, byte[] packet, Dictionary<string, string> headers)
        {
            lock (_lock)
            {
                IBasicProperties basicProperties = _model.CreateBasicProperties();
                basicProperties.SetPersistent(true);

                var messageHeaders = GetHeaders(typeof(byte[]), headers, endPoint, "ByteStream");

                var envelope = new Envelope
                {
                    Body = packet,
                    Headers = messageHeaders
                };

                basicProperties.Headers = envelope.Headers;
                basicProperties.MessageId = basicProperties.Headers["MessageId"].ToString(); // keep track of retries

                Retry.Do(() => _model.BasicPublish(string.Empty, endPoint, basicProperties, envelope.Body),
                         ex => RetryConnection(),
                         new TimeSpan(0, 0, 0, 6), 10);
            }
        }

        private string ConfigureExchange(string exchangeName)
        {
            try
            {
                _model.ExchangeDeclare(exchangeName, "fanout", true, false, null);
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring exchange - {0}", ex.Message));
            }

            return exchangeName;
        }
    }
}