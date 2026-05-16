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
internal sealed class Connection(ITransportConfiguration transportSettings, string queueName, ILogger logger) : IAsyncDisposable, IServiceConnectConnection
{
    private IConnection? _connection;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private int _disposed;
    private readonly TimeSpan _disposeLockTimeout = TimeSpan.FromSeconds(30);
    private readonly ConnectionLifecycleHooks _lifecycle = new(logger);

    // Test seam: when set, replaces the call to ConnectionFactory.CreateConnectionAsync
    // with the supplied factory. Mirrors ProducerConnection.CreateConnectionForTests.
    internal Func<ConnectionFactory, string[], string, CancellationToken, Task<IConnection>>? CreateConnectionForTests;

    private readonly string[] _hosts = (transportSettings ?? throw new ArgumentNullException(nameof(transportSettings)))
        .Host?.Split(',') ?? throw new ArgumentException("transportSettings.Host must be set to a non-null comma-separated host list.", nameof(transportSettings));

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _connection) != null)
        {
            return;
        }

        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
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
        var newConnection = await connector(connectionFactory, _hosts, queueName, cancellationToken).ConfigureAwait(false);

        // Race window 1: DisposeAsync may have set _disposed and forced teardown (after a lock-wait
        // timeout) while we were creating. If so, tear down the just-built connection rather than
        // assigning it to a disposed instance.
        if (Volatile.Read(ref _disposed) != 0)
        {
            await TearDownOrphanAsync(newConnection).ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(Connection),
                "Connection was disposed while a connection create was in flight; the just-built connection has been torn down.");
        }

        _connection = newConnection;

        // Race window 2: DisposeAsync may have timed out on the connection lock between our
        // check above and the assignment, then read _connection as null (the prior value) and
        // returned without tearing down. Re-check after assigning and clean up if so — we
        // exchange to null so our orphan-teardown does not race a DisposeAsync that finally
        // acquires the lock and sees the assigned-then-nulled value. This mirrors the pattern
        // in ProducerConnection.CreateConnectionAsync.
        if (Volatile.Read(ref _disposed) != 0)
        {
            var orphan = Interlocked.Exchange(ref _connection, null);
            await TearDownOrphanAsync(orphan).ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(Connection),
                "Connection was disposed while a connection create was in flight; the just-built connection has been torn down.");
        }

        _lifecycle.Attach(_connection);
        // VirtualHost is set on the ConnectionFactory (and thus the connection) but is not
        // surfaced on AmqpTcpEndpoint. Read it from the transport config — the value the
        // factory was built with is exactly what the broker will route against.
        var (host, port) = ConnectionLifecycleHooks.ResolveEndpoint(_connection);
        RabbitMqClientLog.ConnectionOpened(
            logger,
            host,
            port,
            string.IsNullOrEmpty(transportSettings.VirtualHost) ? "/" : transportSettings.VirtualHost,
            _connection.ClientProvidedName ?? string.Empty);
    }

    private ConnectionFactory BuildConnectionFactory() =>
        ConnectionFactoryBuilder.Build(transportSettings, logger);

    private async Task TearDownOrphanAsync(IConnection? orphan)
    {
        if (orphan is null)
        {
            return;
        }
        try
        {
            if (orphan.IsOpen)
            {
                await orphan.CloseAsync().ConfigureAwait(false);
            }
            orphan.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error tearing down orphan connection after dispose-during-create race");
        }
    }

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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var conn = Volatile.Read(ref _connection);
        if (conn == null)
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
            // Read _connection BEFORE re-checking _disposed: a concurrent DisposeAsync sets
            // _disposed=1 then nulls _connection. Reading _disposed first leaves a window
            // where the disposal-check passes and the subsequent _connection read returns
            // null — surfacing a misleading InvalidOperationException instead of the
            // canonical ObjectDisposedException. Reading _connection first and only
            // consulting _disposed on the null branch closes the window: a null _connection
            // post-ConnectAsync can only be the result of an interleaving dispose.
            conn = Volatile.Read(ref _connection);
            if (conn is null)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    throw new ObjectDisposedException(nameof(Connection));
                }
                throw new InvalidOperationException("Connection was not initialized.");
            }
        }

        try
        {
            return await conn.CreateChannelAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
            // Concurrent DisposeAsync tore down the underlying IConnection mid-call. The
            // RabbitMQ.Client ODE carries ObjectName="IConnection" which leaks the inner
            // type and breaks ObjectName-based callers; surface this Connection's name so
            // the "our instance was disposed" signal is consistent across all race paths.
            throw new ObjectDisposedException(nameof(Connection));
        }
    }

    /// <summary>
    /// Closes and disposes the underlying RabbitMQ connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Share a single stopwatch budget across lock wait + connection close so the
        // worst-case dispose latency is bounded by _disposeLockTimeout, not 2x. Without
        // the shared budget a stalled broker swallowing close frames hangs DisposeAsync
        // indefinitely after the semaphore wait, breaking container-orchestrated SIGTERM
        // grace windows.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        IConnection? conn = null;
        var acquired = await _connectionLock.WaitAsync(_disposeLockTimeout).ConfigureAwait(false);
        try
        {
            if (!acquired)
            {
                logger.LogWarning(
                    "Connection.DisposeAsync timed out waiting for the connection lock after {Timeout}; forcing disposal.",
                    _disposeLockTimeout);
            }
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
                // Detach BEFORE close so the broker-driven ConnectionShutdownAsync that fires
                // inside CloseAsync is not re-emitted as a ConnectionLost log entry. The handler
                // detach is paired with the matching attach in CreateConnectionCoreAsync against
                // this same IConnection reference.
                _lifecycle.Detach(conn);

                if (conn.IsOpen)
                {
                    var remaining = _disposeLockTimeout - stopwatch.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        // Budget exhausted by the lock wait — issue a synchronous close with a
                        // minimal timeout so we don't hang. The broker may still drop the close
                        // frame, but we do not wait on the result.
                        remaining = TimeSpan.FromMilliseconds(100);
                    }
                    using var closeCts = new CancellationTokenSource(remaining);
                    try
                    {
                        await conn.CloseAsync(closeCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (closeCts.IsCancellationRequested)
                    {
                        logger.LogWarning(
                            "Connection.DisposeAsync timed out closing the connection within the remaining {Remaining} budget; proceeding with disposal.",
                            remaining);
                    }
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
