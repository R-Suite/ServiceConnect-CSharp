using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

public interface IServiceConnectConnection
{
    void Connect();
    IModel CreateModel();
    void Dispose();
    bool IsConnected();
}

public class Connection : IDisposable, IServiceConnectConnection
{
    private readonly ITransportConfiguration _transportSettings;
    private IConnection? _connection;
    private readonly object _connectionLock = new();

    private readonly string _queueName;
    private readonly ILogger _logger;
    private readonly bool _heartbeatEnabled;
    private readonly TimeSpan _heartbeatTime;
    private readonly string[] _hosts;

    public Connection(ITransportConfiguration transportSettings, string queueName, ILogger logger)
    {
        _hosts = transportSettings.Host.Split(',');
        _transportSettings = transportSettings;
        _queueName = queueName;
        _logger = logger;
        _heartbeatEnabled = !transportSettings.ClientSettings.ContainsKey("HeartbeatEnabled") || (bool)transportSettings.ClientSettings["HeartbeatEnabled"];
        _heartbeatTime = transportSettings.ClientSettings.ContainsKey("HeartbeatTime") ? new TimeSpan(0, 0, (int)transportSettings.ClientSettings["HeartbeatTime"]) : new TimeSpan(0, 0, 120);
    }

    public void Connect()
    {
        if (_connection == null)
        {
            lock (_connectionLock)
            {
                if (_connection == null)
                    CreateConnection();
            }
        }
    }

    private void CreateConnection()
    {
        lock (_connectionLock)
        {
            if (_connection != null)
                return;

            _logger.LogDebug("Creating connection to queue {QueueName}", _queueName);

            var connectionFactory = BuildConnectionFactory();
            _connection = connectionFactory.CreateConnection(_hosts, _queueName);
        }
    }

    private ConnectionFactory BuildConnectionFactory()
    {
        var port = _transportSettings.ClientSettings.ContainsKey("Port")
            ? Convert.ToInt32(_transportSettings.ClientSettings["Port"])
            : AmqpTcpEndpoint.UseDefaultPort;

        var factory = new ConnectionFactory
        {
            VirtualHost = "/",
            Port = port,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            RequestedHeartbeat = _heartbeatEnabled ? _heartbeatTime : TimeSpan.Zero,
            DispatchConsumersAsync = true
        };

        if (!string.IsNullOrEmpty(_transportSettings.Username))
        {
            factory.UserName = _transportSettings.Username;
        }

        if (!string.IsNullOrEmpty(_transportSettings.Password))
        {
            factory.Password = _transportSettings.Password;
        }

        if (_transportSettings.SslEnabled)
        {
            factory.Ssl = SslConfigurationBuilder.BuildSslOptions(_transportSettings);
            factory.Port = AmqpTcpEndpoint.DefaultAmqpSslPort;
        }

        if (!string.IsNullOrEmpty(_transportSettings.VirtualHost))
        {
            factory.VirtualHost = _transportSettings.VirtualHost;
        }

        return factory;
    }

    public bool IsConnected()
    {
        return _connection?.IsOpen ?? false;
    }

    public IModel CreateModel()
    {
        if (_connection == null)
            CreateConnection();

        return _connection!.CreateModel();
    }

    public void Dispose()
    {
        if (_connection == null) return;
        _connection.Abort();
        _connection = null;
    }
}
