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
using Common.Logging;
using ServiceConnect.Interfaces;
using RabbitMQ.Client;

namespace ServiceConnect.Client.RabbitMQ
{
    public class Producer : IProducer
    {
        private readonly ITransportSettings _transportSettings;
        private readonly IDictionary<string, IList<string>> _queueMappings;
        private IModel _model;
        private IConnection _connection;
        private readonly Object _lock = new Object();
        private static readonly ILog Logger = LogManager.GetLogger(typeof(Producer));
        private ConnectionFactory _connectionFactory;
        private readonly string[] _hosts;
        private readonly long _maxMessageSize;
        private readonly ushort _retryCount;
        private readonly ushort _retryTimeInSeconds;

        public Producer(ITransportSettings transportSettings, IDictionary<string, IList<string>> queueMappings)
        {
            _transportSettings = transportSettings;
            _queueMappings = queueMappings;
            _maxMessageSize = transportSettings.ClientSettings.ContainsKey("MessageSize") ? Convert.ToInt64(_transportSettings.ClientSettings["MessageSize"]) : 65536;
            _hosts = transportSettings.Host.Split(',');
            _retryCount = transportSettings.ClientSettings.ContainsKey("RetryCount") ? Convert.ToUInt16((int)transportSettings.ClientSettings["RetryCount"]) : Convert.ToUInt16(60);
            _retryTimeInSeconds = transportSettings.ClientSettings.ContainsKey("RetrySeconds") ? Convert.ToUInt16((int)transportSettings.ClientSettings["RetrySeconds"]) : Convert.ToUInt16(10);

            Retry.Do(CreateConnection, ex =>
            {
                DisposeConnection();
            }, new TimeSpan(0, 0, 0, _retryTimeInSeconds), _retryCount);
        }
        
        private void CreateConnection()
        {
            _connectionFactory = new ConnectionFactory
            {
                VirtualHost = "/",
                Protocol = Protocols.DefaultProtocol,
                Port = AmqpTcpEndpoint.UseDefaultPort,
                UseBackgroundThreadsForIO = true,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true
            };

            if (!string.IsNullOrEmpty(_transportSettings.Username))
            {
                _connectionFactory.UserName = _transportSettings.Username;
            }

            if (!string.IsNullOrEmpty(_transportSettings.Password))
            {
                _connectionFactory.Password = _transportSettings.Password;
            }

            if (_transportSettings.SslEnabled)
            {
                _connectionFactory.Ssl = new SslOption
                {
                    Enabled = true,
                    AcceptablePolicyErrors = _transportSettings.AcceptablePolicyErrors,
                    ServerName = _transportSettings.ServerName,
                    CertPassphrase = _transportSettings.CertPassphrase,
                    CertPath = _transportSettings.CertPath,
                    Certs = _transportSettings.Certs,
                    CertificateSelectionCallback = _transportSettings.CertificateSelectionCallback,
                    CertificateValidationCallback = _transportSettings.CertificateValidationCallback
                };
                _connectionFactory.Port = AmqpTcpEndpoint.DefaultAmqpSslPort;
            }

            if (!string.IsNullOrEmpty(_transportSettings.VirtualHost))
            {
                _connectionFactory.VirtualHost = _transportSettings.VirtualHost;
            }

            var producerName = Assembly.GetEntryAssembly() != null ? Assembly.GetEntryAssembly().GetName().Name : System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            
            _connection = _connectionFactory.CreateConnection(_hosts, producerName);
            _model = _connection.CreateModel();
        }

        public void Publish(Type type, byte[] message, Dictionary<string, string> headers = null)
        {
            DoPublish(type, message, headers);
        }

        private void DoPublish(Type type, byte[] message, Dictionary<string, string> headers)
        {
            lock (_lock)
            {
                IBasicProperties basicProperties = _model.CreateBasicProperties();

                var messageHeaders = GetHeaders(type, headers, _transportSettings.QueueName, "Publish");

                var envelope = new Envelope
                {
                    Body = message,
                    Headers = messageHeaders
                };

                basicProperties.Headers = envelope.Headers;
                basicProperties.MessageId = basicProperties.Headers["MessageId"].ToString(); // keep track of retries
                basicProperties.Persistent = true;

                string exchName = type.FullName.Replace(".", string.Empty);
                var exchangeName = ConfigureExchange(exchName, "fanout");

                Retry.Do(() => _model.BasicPublish(exchangeName, "", basicProperties, envelope.Body),
                ex =>
                {
                    Logger.Error("Error publishing message", ex);
                    DisposeConnection();
                    RetryConnection();
                }, new TimeSpan(0, 0, 0, _retryTimeInSeconds), _retryCount);
            }
        }

        private void RetryConnection()
        {
            Logger.Debug("In Producer.RetryConnection()");
            CreateConnection();
        }

        public void Send(Type type, byte[] message, Dictionary<string, string> headers = null)
        {
            lock (_lock)
            {
                IBasicProperties basicProperties = _model.CreateBasicProperties();
                basicProperties.Persistent = true;

                IList<string> endPoints = _queueMappings[type.FullName];

                foreach (string endPoint in endPoints)
                {
                    Dictionary<string, object> messageHeaders = GetHeaders(type, headers, endPoint, "Send");

                    basicProperties.Headers = messageHeaders;
                    basicProperties.MessageId = basicProperties.Headers["MessageId"].ToString(); // keep track of retries

                    Retry.Do(() => _model.BasicPublish(string.Empty, endPoint, basicProperties, message),
                    ex =>
                    {
                        DisposeConnection();
                        RetryConnection();
                    },
                    new TimeSpan(0, 0, 0, _retryTimeInSeconds), _retryCount);
                }
            }
        }

        public void Send(string endPoint, Type type, byte[] message, Dictionary<string, string> headers = null)
        {
            lock (_lock)
            {
                IBasicProperties basicProperties = _model.CreateBasicProperties();
                basicProperties.Persistent = true;

                var messageHeaders = GetHeaders(type, headers, endPoint, "Send");

                basicProperties.Headers = messageHeaders;
                basicProperties.MessageId = basicProperties.Headers["MessageId"].ToString(); // keep track of retries

                Retry.Do(() => _model.BasicPublish(string.Empty, endPoint, basicProperties, message),
                ex =>
                {
                    DisposeConnection();
                    RetryConnection();
                },
                new TimeSpan(0, 0, 0, _retryTimeInSeconds), _retryCount);
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
            headers["TypeName"] = type.FullName;
            headers["FullTypeName"] = type.AssemblyQualifiedName;
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
                try
                {
                    Logger.Debug("Disposing Model");
                    _model.Dispose();
                    _model = null;
                }
                catch (Exception ex)
                {
                    Logger.Warn("Error disposing model", ex);
                }
            }

            if (_connection != null)
            {
                try
                {
                    Logger.Debug("Disposing connection");
                    _connection.Dispose();
                }
                catch (Exception ex)
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
                basicProperties.Persistent = true;

                var messageHeaders = GetHeaders(typeof(byte[]), headers, endPoint, "ByteStream");

                var envelope = new Envelope
                {
                    Body = packet,
                    Headers = messageHeaders
                };

                basicProperties.Headers = envelope.Headers;
                basicProperties.MessageId = basicProperties.Headers["MessageId"].ToString(); // keep track of retries

                Retry.Do(() => _model.BasicPublish(string.Empty, endPoint, basicProperties, envelope.Body),
                ex =>
                {
                    DisposeConnection();
                    RetryConnection();
                },
                new TimeSpan(0, 0, 0, _retryTimeInSeconds), _retryCount);
            }
        }

        private string ConfigureExchange(string exchangeName, string type)
        {
            try
            {
                _model.ExchangeDeclare(exchangeName, type, true, false, null);
            }
            catch (Exception ex)
            {
                Logger.Warn(string.Format("Error declaring exchange - {0}", ex.Message));
            }

            return exchangeName;
        }

        private void DisposeConnection()
        {
            try
            {
                if (_connection != null)
                {
                    lock (_connection)
                    {
                        if (_connection != null && _connection.IsOpen)
                        {
                            _connection.Close();
                            _connection.Dispose();
                            _connection = null;
                        }
                    }
                }
                
            }
            catch (Exception e)
            {
                Logger.Warn("Exception trying to close connection", e);
            }

            try
            {
                if (_model != null)
                {
                    lock (_model)
                    {
                        if (_model != null && _model.IsOpen)
                        {
                            _model.Close();
                            _model.Dispose();
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Warn("Exception trying to close model", e);
            }
        }

    }
}