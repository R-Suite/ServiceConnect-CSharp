using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

public sealed class Connection(ITransportConfiguration transportSettings, string queueName, ILogger logger) : IAsyncDisposable, IServiceConnectConnection
{
    private IConnection? _connection;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private readonly bool _heartbeatEnabled = !transportSettings.ClientSettings.TryGetValue(RabbitMQSettingKeys.HeartbeatEnabled, out var hbEnabled) || (bool)hbEnabled;
    private readonly TimeSpan _heartbeatTime = transportSettings.ClientSettings.TryGetValue(RabbitMQSettingKeys.HeartbeatTime, out var hbTime) ? new TimeSpan(0, 0, (int)hbTime) : new TimeSpan(0, 0, 120);
    private readonly string[] _hosts = transportSettings.Host.Split(',');

    public void Connect()
    {
        if (_connection != null) return;

        _connectionLock.Wait();
        try
        {
            if (_connection == null)
                CreateConnectionCore();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private void CreateConnectionCore()
    {
        logger.LogDebug("Creating connection to queue {QueueName}", queueName);

        var connectionFactory = BuildConnectionFactory();
        _connection = connectionFactory.CreateConnectionAsync(_hosts, queueName).GetAwaiter().GetResult();
    }

    private ConnectionFactory BuildConnectionFactory()
    {
        var port = transportSettings.ClientSettings.TryGetValue(RabbitMQSettingKeys.Port, out var portVal)
            ? Convert.ToInt32(portVal)
            : AmqpTcpEndpoint.UseDefaultPort;

        var factory = new ConnectionFactory
        {
            VirtualHost = "/",
            Port = port,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            RequestedHeartbeat = _heartbeatEnabled ? _heartbeatTime : TimeSpan.Zero
        };

        if (!string.IsNullOrEmpty(transportSettings.Username))
        {
            factory.UserName = transportSettings.Username;
        }

        if (!string.IsNullOrEmpty(transportSettings.Password))
        {
            factory.Password = transportSettings.Password;
        }

        if (transportSettings.SslEnabled)
        {
            factory.Ssl = SslConfigurationBuilder.BuildSslOptions(transportSettings);
            factory.Port = AmqpTcpEndpoint.DefaultAmqpSslPort;
        }

        if (!string.IsNullOrEmpty(transportSettings.VirtualHost))
        {
            factory.VirtualHost = transportSettings.VirtualHost;
        }

        return factory;
    }

    public bool IsConnected()
    {
        return _connection?.IsOpen ?? false;
    }

    public async Task<IChannel> CreateChannelAsync()
    {
        if (_connection == null)
            Connect();

        return await _connection!.CreateChannelAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection == null) return;

        var conn = _connection;
        _connection = null;
        try
        {
            if (conn.IsOpen)
                await conn.CloseAsync();
            conn.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error closing connection during async dispose");
        }
    }

    public void Dispose()
    {
        if (_connection == null) return;

        var conn = _connection;
        _connection = null;
        _ = Task.Run(async () =>
        {
            try
            {
                if (conn.IsOpen)
                    await conn.CloseAsync();
                conn.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error closing connection during dispose");
            }
        });
    }
}
