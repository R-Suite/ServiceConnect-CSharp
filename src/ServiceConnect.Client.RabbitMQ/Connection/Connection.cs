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
    private readonly TimeSpan _disposeLockTimeout = TimeSpan.FromSeconds(30);

    // Test seam: when set, replaces the call to ConnectionFactory.CreateConnectionAsync
    // with the supplied factory. Mirrors ProducerConnection.CreateConnectionForTests.
    internal Func<ConnectionFactory, string[], string, CancellationToken, Task<IConnection>>? CreateConnectionForTests;

    private readonly string[] _hosts = transportSettings.Host.Split(',');

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _connection) != null)
        {
            return;
        }

        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Volatile.Read(ref _connection) == null)
            {
                await CreateConnectionCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task CreateConnectionCoreAsync(CancellationToken cancellationToken)
    {
        logger.LogDebug("Creating connection to queue {QueueName}", queueName);
        var connectionFactory = BuildConnectionFactory();
        var connector = CreateConnectionForTests ?? ((f, h, n, ct) => f.CreateConnectionAsync(h, n, ct));
        _connection = await connector(connectionFactory, _hosts, queueName, cancellationToken).ConfigureAwait(false);
    }

    private ConnectionFactory BuildConnectionFactory() =>
        ConnectionFactoryBuilder.Build(transportSettings);

    /// <summary>
    /// Determines whether the underlying RabbitMQ connection is open.
    /// </summary>
    /// <returns><see langword="true"/> when the connection is open; otherwise, <see langword="false"/>.</returns>
    public bool IsConnected()
    {
        return Volatile.Read(ref _connection)?.IsOpen ?? false;
    }

    /// <summary>
    /// Returns the underlying <see cref="IConnection"/>, or <see langword="null"/> if not yet established or already disposed.
    /// </summary>
    public IConnection? UnderlyingConnection => Volatile.Read(ref _connection);

    /// <summary>
    /// Creates a RabbitMQ channel, establishing the connection first if needed.
    /// </summary>
    /// <returns>A newly created channel.</returns>
    public Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default)
        => CreateChannelAsync(options: null, cancellationToken);

    /// <summary>
    /// Creates a RabbitMQ channel with the supplied options, establishing the connection first if needed.
    /// </summary>
    /// <param name="options">Channel options applied to the underlying RabbitMQ channel, or <see langword="null"/> for defaults.</param>
    /// <param name="cancellationToken">A token used to cancel connection-establishment and channel-open operations.</param>
    /// <returns>A newly created channel.</returns>
    public async Task<IChannel> CreateChannelAsync(CreateChannelOptions? options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var conn = Volatile.Read(ref _connection);
        if (conn == null)
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
            conn = Volatile.Read(ref _connection)
                ?? throw new InvalidOperationException("Connection was not initialized.");
        }

        return await conn.CreateChannelAsync(options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes and disposes the underlying RabbitMQ connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        IConnection? conn = null;
        var acquired = await _connectionLock.WaitAsync(_disposeLockTimeout).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            if (!acquired)
            {
                logger.LogWarning(
                    "Connection.DisposeAsync timed out waiting for the connection lock after {Timeout}; forcing disposal.",
                    _disposeLockTimeout);
            }
            _disposed = true;
            conn = _connection;
            _connection = null;
        }
        finally
        {
            if (acquired)
            {
                _connectionLock.Release();
            }
        }

        if (conn != null)
        {
            try
            {
                if (conn.IsOpen)
                {
                    await conn.CloseAsync().ConfigureAwait(false);
                }

                conn.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error closing connection during async dispose");
            }
        }

        // _connectionLock is intentionally NOT Disposed:
        // SemaphoreSlim.Dispose only releases the lazily-allocated WaitHandle, and
        // we never call AvailableWaitHandle, so disposal is a functional no-op. A
        // concurrent ConnectAsync's `finally { Release(); }` running on a disposed
        // semaphore would throw ObjectDisposedException out of the unwind path,
        // which we cannot prevent without holding GC references to every caller.
        // The field is GC'd with this Connection instance.
    }

}
