using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Manages a RabbitMQ connection for ServiceConnect producers and consumers.
/// </summary>
/// <param name="transportSettings">The transport settings used to configure the connection factory.</param>
/// <param name="queueName">The client-provided connection name used by RabbitMQ.</param>
/// <param name="logger">The logger used for connection lifecycle events.</param>
public sealed class Connection(ITransportConfiguration transportSettings, string queueName, ILogger logger) : IAsyncDisposable, IServiceConnectConnection
{
    private IConnection? _connection;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private volatile bool _disposed;

    private readonly bool _heartbeatEnabled = !transportSettings.ClientSettings.TryGetValue(RabbitMQSettingKeys.HeartbeatEnabled, out var hbEnabled) || (bool)hbEnabled;
    private readonly TimeSpan _heartbeatTime = transportSettings.ClientSettings.TryGetValue(RabbitMQSettingKeys.HeartbeatTime, out var hbTime) ? new TimeSpan(0, 0, (int)hbTime) : new TimeSpan(0, 0, 120);
    private readonly string[] _hosts = transportSettings.Host.Split(',');

    private async Task ConnectAsync()
    {
        if (Volatile.Read(ref _connection) != null) return;

        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Volatile.Read(ref _connection) == null)
                await CreateConnectionCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task CreateConnectionCoreAsync()
    {
        logger.LogDebug("Creating connection to queue {QueueName}", queueName);
        var connectionFactory = BuildConnectionFactory();
        _connection = await connectionFactory.CreateConnectionAsync(_hosts, queueName).ConfigureAwait(false);
    }

    private ConnectionFactory BuildConnectionFactory() =>
        ConnectionFactoryBuilder.Build(
            transportSettings,
            _heartbeatEnabled ? _heartbeatTime : TimeSpan.Zero);

    /// <summary>
    /// Determines whether the underlying RabbitMQ connection is open.
    /// </summary>
    /// <returns><see langword="true"/> when the connection is open; otherwise, <see langword="false"/>.</returns>
    public bool IsConnected()
    {
        return _connection?.IsOpen ?? false;
    }

    /// <summary>
    /// Creates a RabbitMQ channel, establishing the connection first if needed.
    /// </summary>
    /// <returns>A newly created channel.</returns>
    public async Task<IChannel> CreateChannelAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var conn = Volatile.Read(ref _connection);
        if (conn == null)
        {
            await ConnectAsync().ConfigureAwait(false);
            conn = Volatile.Read(ref _connection)
                ?? throw new InvalidOperationException("Connection was not initialized.");
        }

        return await conn.CreateChannelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Closes and disposes the underlying RabbitMQ connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        IConnection? conn;
        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            conn = _connection;
            _connection = null;
        }
        finally
        {
            _connectionLock.Release();
        }

        if (conn != null)
        {
            try
            {
                if (conn.IsOpen)
                    await conn.CloseAsync().ConfigureAwait(false);
                conn.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error closing connection during async dispose");
            }
        }

        _connectionLock.Dispose();
    }

}
