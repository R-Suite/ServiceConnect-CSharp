using System;
using ServiceConnect.Interfaces;
using RabbitMQ.Client;

namespace ServiceConnect.Client.RabbitMQ
{
    public interface IServiceConnectConnection
    {
        void Connect();
        IModel CreateModel();
        void Dispose();
        bool IsConnected();
    }

    public class Connection : IDisposable, IServiceConnectConnection
    {
        private readonly ITransportSettings _transportSettings;
        private IConnection _connection;

        private readonly string _queueName;
        private readonly ILogger _logger;
        private readonly bool _heartbeatEnabled;
        private readonly ushort _heartbeatTime;
        private readonly string[] _hosts;

        public Connection(ITransportSettings transportSettings, string queueName, ILogger logger)
        {
            _hosts = transportSettings.Host.Split(',');
            _transportSettings = transportSettings;
            _queueName = queueName;
            _logger = logger;
            _transportSettings = transportSettings;
            _heartbeatEnabled = !transportSettings.ClientSettings.ContainsKey("HeartbeatEnabled") || (bool)transportSettings.ClientSettings["HeartbeatEnabled"];
            _heartbeatTime = transportSettings.ClientSettings.ContainsKey("HeartbeatTime") ? Convert.ToUInt16((int)transportSettings.ClientSettings["HeartbeatTime"]) : Convert.ToUInt16(120);
        }

        public void Connect()
        {
            if (_connection == null)
                CreateConnection();
        }

        private void CreateConnection()
        {
            _logger.Debug(string.Format("Creating connection to queue {0}", _queueName));

            var connectionFactory = new ConnectionFactory
            {
                Protocol = Protocols.DefaultProtocol,
                Port = AmqpTcpEndpoint.UseDefaultPort,
                UseBackgroundThreadsForIO = true,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                DispatchConsumersAsync = true
            };
            
            if (_heartbeatEnabled)
            {
                connectionFactory.RequestedHeartbeat = _heartbeatTime;
            }

            if (!string.IsNullOrEmpty(_transportSettings.Username))
            {
                connectionFactory.UserName = _transportSettings.Username;
            }

            if (!string.IsNullOrEmpty(_transportSettings.Password))
            {
                connectionFactory.Password = _transportSettings.Password;
            }

            if (_transportSettings.SslEnabled)
            {
                connectionFactory.Ssl = new SslOption
                {
                    Version = _transportSettings.Version,
                    Enabled = true,
                    AcceptablePolicyErrors = _transportSettings.AcceptablePolicyErrors,
                    ServerName = _transportSettings.ServerName,
                    CertPassphrase = _transportSettings.CertPassphrase,
                    CertPath = _transportSettings.CertPath,
                    Certs = _transportSettings.Certs,
                    CertificateSelectionCallback = _transportSettings.CertificateSelectionCallback,
                    CertificateValidationCallback = _transportSettings.CertificateValidationCallback
                };
                connectionFactory.Port = AmqpTcpEndpoint.DefaultAmqpSslPort;
            }

            if (!string.IsNullOrEmpty(_transportSettings.VirtualHost))
            {
                connectionFactory.VirtualHost = _transportSettings.VirtualHost;
            }
            _connection = connectionFactory.CreateConnection(_hosts, _queueName);
        }

        public bool IsConnected()
        {
            return _connection?.IsOpen ?? false;
        }

        public IModel CreateModel()
        {
            if (_connection == null)
                CreateConnection();

            return _connection.CreateModel();
        }

        public void Dispose()
        {
            if (_connection == null) return;
            _connection.Abort(500);
            _connection = null;
        }
    }
}
