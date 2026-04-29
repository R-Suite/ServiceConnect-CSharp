using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Owns the RabbitMQ <see cref="IConnection"/> and <see cref="IChannel"/> used by
/// <see cref="Producer"/> to publish, plus the per-connection caches (declared
/// exchanges) and reconnect/teardown lifecycle. Producer delegates connection
/// concerns here and keeps publish orchestration to itself.
/// </summary>
internal sealed class ProducerConnection
{
    /// <summary>Default publish-retry attempt count.</summary>
    private const ushort DefaultRetryCount = 60;
    /// <summary>Default delay between publish retries, in seconds.</summary>
    private const ushort DefaultRetryTimeInSeconds = 10;

    // Cache process/assembly name — computed once at startup, reused on every reconnect.
    private static readonly string ProducerName = Assembly.GetEntryAssembly()?.GetName().Name
        ?? System.Diagnostics.Process.GetCurrentProcess().ProcessName;

    private readonly ITransportConfiguration _transportConfiguration;
    private readonly ILogger _logger;
    private readonly string[] _hosts;
    private readonly ushort _retryCount;
    private readonly ushort _retryTimeInSeconds;
    private readonly bool _publisherAcks;
    // Track which exchange names have already been declared on the current connection.
    // Cleared on reconnect because exchange state is per-connection.
    private readonly ConcurrentDictionary<string, bool> _declaredExchanges = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connectionSemaphore = new(1, 1);

    private ConnectionFactory? _connectionFactory;
    private volatile IChannel? _model;
    private IConnection? _connection;
    private volatile bool _connected;

    // Test hooks consumed by Producer's pass-through properties. Setting these on
    // Producer routes through to here so existing test code (`producer.ReconnectForTests = ...`)
    // is unchanged.
    internal Func<CancellationToken, Task>? ReconnectForTests;
    internal Func<ConnectionFactory, string[], string, CancellationToken, Task<IConnection>>? CreateConnectionForTests;

    public ProducerConnection(ITransportConfiguration transportConfiguration, ILogger logger)
    {
        _transportConfiguration = transportConfiguration;
        _logger = logger;

        var settings = transportConfiguration.ClientSettings;
        _hosts = transportConfiguration.Host.Split(',');
        _retryCount = GetSetting(settings, RabbitMQSettingKeys.RetryCount, DefaultRetryCount, Convert.ToUInt16);
        _retryTimeInSeconds = GetSetting(settings, RabbitMQSettingKeys.RetrySeconds, DefaultRetryTimeInSeconds, Convert.ToUInt16);
        _publisherAcks = GetSetting(settings, RabbitMQSettingKeys.PublisherAcknowledgements, false, Convert.ToBoolean);
    }

    private static T GetSetting<T>(IReadOnlyDictionary<string, object> settings, string key, T defaultValue, Func<object, T> converter)
    {
        return settings.TryGetValue(key, out var value) ? converter(value) : defaultValue;
    }

    /// <summary>
    /// The current RabbitMQ channel. Caller is responsible for ensuring the connection is
    /// established (via <see cref="EnsureConnectedAsync"/>) and for serializing publishes
    /// against the channel.
    /// </summary>
    public IChannel Channel => _model ?? throw new InvalidOperationException(
        "ProducerConnection.Channel accessed before EnsureConnectedAsync established a channel.");

    /// <summary>
    /// Returns true only when both the connected flag is set AND the underlying channel
    /// is still open. A broker drop closes the channel without clearing the flag, so
    /// checking the flag alone would permanently suppress reconnect attempts.
    /// </summary>
    public bool IsHealthy() => _connected && (_model?.IsOpen ?? false);

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (IsHealthy())
        {
            return;
        }

        await _connectionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsHealthy())
            {
                return;
            }

            await Retry.DoAsync(() => CreateConnectionAsync(cancellationToken), async ex =>
            {
                _logger.LogError(ex, "Error creating connection");
                await TearDownChannelAndConnectionAsync().ConfigureAwait(false);
            }, TimeSpan.FromSeconds(_retryTimeInSeconds), _retryCount, cancellationToken).ConfigureAwait(false);

            _connected = true;
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    /// <summary>
    /// Declares the named exchange on the current channel if it has not already been
    /// declared on this connection. Idempotent within a connection's lifetime; the
    /// per-connection cache is cleared on every (re)connect.
    /// </summary>
    public async Task EnsureExchangeDeclaredAsync(string exchangeName, string type, CancellationToken cancellationToken)
    {
        if (_declaredExchanges.ContainsKey(exchangeName))
        {
            return;
        }

        await _model!.ExchangeDeclareAsync(exchangeName, type, true, false, null, false, false, cancellationToken).ConfigureAwait(false);
        _declaredExchanges[exchangeName] = true;
    }

    /// <summary>
    /// Tears down the current connection/channel and re-establishes them. Used after a
    /// publish-time failure (transport error, channel error, publish timeout) to clear
    /// any half-open state before retrying.
    /// </summary>
    public async Task ReconnectAsync(Exception ex, CancellationToken cancellationToken)
    {
        _logger.LogError(ex, "Error publishing message");

        await DisposeConnectionAsync(cancellationToken).ConfigureAwait(false);
        _declaredExchanges.Clear();

        if (ReconnectForTests != null)
        {
            await ReconnectForTests(cancellationToken).ConfigureAwait(false);
            return;
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the connection cooperatively, waiting up to <paramref name="timeoutBudget"/>
    /// for any in-flight (re)connect to complete before forcing teardown.
    /// </summary>
    public async Task CloseAsync(TimeSpan timeoutBudget)
    {
        var connectionLockAcquired = false;
        try
        {
            connectionLockAcquired = await _connectionSemaphore.WaitAsync(timeoutBudget).ConfigureAwait(false);
            if (!connectionLockAcquired)
            {
                _logger.LogWarning(
                    "ProducerConnection close could not acquire connection lock within {Timeout}; forcing teardown",
                    timeoutBudget);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ProducerConnection close lock-wait failed; forcing teardown");
        }
        finally
        {
            // Best-effort teardown ALWAYS runs, whether or not we held the lock.
            try { await TearDownChannelAndConnectionAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "ProducerConnection channel/connection close failed"); }

            if (connectionLockAcquired)
            {
                _connectionSemaphore.Release();
            }

            // _connectionSemaphore is intentionally NOT Disposed: SemaphoreSlim.Dispose only
            // releases the lazily-allocated WaitHandle, and we never call AvailableWaitHandle,
            // so disposal is a functional no-op. The field is GC'd with this instance.
        }
    }

    private async Task CreateConnectionAsync(CancellationToken cancellationToken)
    {
        _connectionFactory = ConnectionFactoryBuilder.Build(_transportConfiguration);

        // Exchange declarations are per-connection — reset the cache on every (re)connect.
        _declaredExchanges.Clear();

        IConnection? connection = null;
        IChannel? model = null;

        try
        {
            if (CreateConnectionForTests != null)
            {
                connection = await CreateConnectionForTests(_connectionFactory, _hosts, ProducerName, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                connection = await _connectionFactory.CreateConnectionAsync(_hosts, ProducerName, cancellationToken).ConfigureAwait(false);
            }

            if (_publisherAcks)
            {
                var channelOptions = new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true);
                model = await connection.CreateChannelAsync(channelOptions, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                model = await connection.CreateChannelAsync(null, cancellationToken).ConfigureAwait(false);
            }

            _connection = connection;
            _model = model;
        }
        catch
        {
            await DisposeModelAsync(model).ConfigureAwait(false);
            await DisposeConnectionInstanceAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    private async Task DisposeConnectionAsync(CancellationToken cancellationToken)
    {
        var connectionLockAcquired = false;
        try
        {
            // Bound the wait: a wedged in-flight (re)connect cannot stall this dispose.
            // Token honours the publish path's cancellation; 30s ceiling keeps callers that
            // pass a never-cancelled token from blocking indefinitely.
            // See learn/operations/cancellation for the discipline.
            connectionLockAcquired = await _connectionSemaphore
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
            if (!connectionLockAcquired)
            {
                _logger.LogWarning(
                    "ProducerConnection.DisposeConnectionAsync timed out waiting for the connection semaphore; forcing teardown.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(
                "ProducerConnection.DisposeConnectionAsync cancelled while waiting for the semaphore; forcing teardown.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ProducerConnection.DisposeConnectionAsync semaphore-wait failed; forcing teardown");
        }
        finally
        {
            // Best-effort teardown ALWAYS runs, whether or not we held the lock — matches CloseAsync.
            try { await TearDownChannelAndConnectionAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "ProducerConnection teardown after dispose failed"); }

            if (connectionLockAcquired)
            {
                _connectionSemaphore.Release();
            }
        }
    }

    private async Task TearDownChannelAndConnectionAsync()
    {
        var model = Interlocked.Exchange(ref _model, null);
        var connection = Interlocked.Exchange(ref _connection, null);

        await DisposeModelAsync(model).ConfigureAwait(false);
        await DisposeConnectionInstanceAsync(connection).ConfigureAwait(false);
        _connected = false;
    }

    private async Task DisposeModelAsync(IChannel? model)
    {
        if (model != null)
        {
            try
            {
                _logger.LogDebug("Disposing Model");
                if (model.IsOpen)
                {
                    await model.CloseAsync().ConfigureAwait(false);
                }

                model.Dispose();
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing model");
            }
        }
    }

    private async Task DisposeConnectionInstanceAsync(IConnection? connection)
    {
        if (connection != null)
        {
            try
            {
                _logger.LogDebug("Disposing connection");
                if (connection.IsOpen)
                {
                    await connection.CloseAsync().ConfigureAwait(false);
                }

                connection.Dispose();
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing connection");
            }
        }
    }
}
