using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.RateLimiting;
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
    /// <summary>Default cap on outstanding publisher confirms when publisher acks are enabled.</summary>
    private const int DefaultMaxOutstandingPublishConfirms = 256;

    // Cache process/assembly name — computed once at startup, reused on every reconnect.
    private static readonly string ProducerName = Assembly.GetEntryAssembly()?.GetName().Name
        ?? System.Diagnostics.Process.GetCurrentProcess().ProcessName;

    private readonly ITransportConfiguration _transportConfiguration;
    private readonly ILogger _logger;
    private readonly ConnectionLifecycleHooks _lifecycle;
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
    private volatile IConnection? _connection;
    private volatile bool _connected;

    // Set by Producer.PublishWithTimeoutAsync when a publish times out (broker confirm did not
    // arrive within the publish budget). The next EnsureConnectedAsync drives the reconnect off
    // the publish lock so concurrent publishers are not blocked behind a worst-case retry budget.
    private int _resetRequired;

    // Set by CloseAsync before its semaphore-wait. CreateConnectionAsync re-checks AFTER assigning
    // _connection/_model so a dispose that timed out on the semaphore (and forced teardown anyway)
    // is followed by the in-flight create tearing down its own just-built connection rather than
    // orphaning it on the disposed instance.
    private int _disposed;

    // Flipped to 1 on the first EnsureConnectedAsync call. Stays true for the producer's lifetime
    // so the health check can distinguish "lazy, not yet tried" from "tried and currently failed".
    private int _hasAttemptedConnection;

    public bool HasAttemptedConnection => Volatile.Read(ref _hasAttemptedConnection) != 0;

    // Test hooks consumed by Producer's pass-through properties. Setting these on
    // Producer routes through to here so existing test code (`producer.ReconnectForTests = ...`)
    // is unchanged.
    internal Func<CancellationToken, Task>? ReconnectForTests;
    internal Func<ConnectionFactory, string[], string, CancellationToken, Task<IConnection>>? CreateConnectionForTests;

    public ProducerConnection(ITransportConfiguration transportConfiguration, ILogger logger)
    {
        _transportConfiguration = transportConfiguration;
        _logger = logger;
        _lifecycle = new ConnectionLifecycleHooks(logger);

        var settings = transportConfiguration.ClientSettings;
        _hosts = transportConfiguration.Host.Split(',');
        _retryCount = GetSetting(settings, RabbitMQSettingKeys.RetryCount, DefaultRetryCount, Convert.ToUInt16);
        _retryTimeInSeconds = GetSetting(settings, RabbitMQSettingKeys.RetrySeconds, DefaultRetryTimeInSeconds, Convert.ToUInt16);
        // Default flipped to true so callers get publisher-confirm gating out of the box.
        // Two safety properties depend on it: PublishWithTimeoutAsync's timeout actually
        // enforces against a stalled broker, and OutboundHeaderBuilder.BuildBasicProperties'
        // aliasing invariant on the SendAsync(Type) fan-out (the broker ack gates the
        // next iteration's re-stamping of baseHeaders) holds. Explicit opt-out via
        // SetClientSetting("PublisherAcknowledgements", false) is still permitted, but the
        // Producer constructor rejects the dangerous combination of acks-off + nonzero
        // PublishTimeout at startup.
        _publisherAcks = GetSetting(settings, RabbitMQSettingKeys.PublisherAcknowledgements, true, Convert.ToBoolean);
    }

    private static T GetSetting<T>(IReadOnlyDictionary<string, object> settings, string key, T defaultValue, Func<object, T> converter)
    {
        return settings.TryGetValue(key, out var value) ? converter(value) : defaultValue;
    }

    /// <summary>
    /// Resolves the cap on outstanding publisher confirms from <c>ClientSettings</c>, falling back
    /// to <see cref="DefaultMaxOutstandingPublishConfirms"/> when the setting is unset. Throws on
    /// non-<c>int</c> or non-positive values so misconfiguration surfaces loudly, consistent with
    /// the convention in <c>ConnectionFactoryBuilder.ConvertSettingToInt32</c>.
    /// </summary>
    internal static int ResolveMaxOutstandingPublishConfirms(ITransportConfiguration transport)
    {
        if (!transport.ClientSettings.TryGetValue(RabbitMQSettingKeys.MaxOutstandingPublishConfirms, out var raw))
        {
            return DefaultMaxOutstandingPublishConfirms;
        }
        if (raw is not int permits)
        {
            throw new InvalidOperationException(
                $"Setting '{RabbitMQSettingKeys.MaxOutstandingPublishConfirms}' must be an int; got value '{raw}' of type '{raw?.GetType().FullName ?? "<null>"}'.");
        }
        if (permits <= 0)
        {
            throw new InvalidOperationException(
                $"Setting '{RabbitMQSettingKeys.MaxOutstandingPublishConfirms}' must be positive; got {permits}.");
        }
        return permits;
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

    /// <summary>
    /// Marks the connection for reset on the next call to <see cref="EnsureConnectedAsync"/>.
    /// Synchronous and idempotent. Used by Producer.PublishWithTimeoutAsync to avoid awaiting
    /// ReconnectAsync while holding the publish lock.
    /// </summary>
    internal void MarkResetRequired() => Interlocked.Exchange(ref _resetRequired, 1);

    /// <summary>Test seam: snapshot of the reset flag for unit-test assertions.</summary>
    internal bool ResetRequiredForTests => Volatile.Read(ref _resetRequired) == 1;

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        // Mark that a connection attempt has begun regardless of outcome. This allows the health
        // check to distinguish "lazy, not yet tried" (pre-publish, still Healthy) from
        // "tried and currently disconnected" (Unhealthy). Set before IsHealthy check so even
        // a reconnect path (reset-required) correctly flips the flag.
        Interlocked.Exchange(ref _hasAttemptedConnection, 1);

        // Atomically consume the reset-required flag set by a prior publish timeout. The
        // ReconnectAsync below holds _connectionSemaphore (NOT the producer's _publishLock),
        // so concurrent publishers waiting on the publish lock are not blocked here. Only one
        // caller succeeds at the Exchange — the rest see flag == 0 and proceed normally.
        if (Interlocked.Exchange(ref _resetRequired, 0) == 1)
        {
            await ReconnectAsync(
                new InvalidOperationException("Channel reset required after publish timeout"),
                cancellationToken).ConfigureAwait(false);
            // ReconnectAsync calls EnsureConnectedAsync internally on the no-test-hook path, so
            // we are already healthy on return. Fall through for explicit safety in case the
            // test-hook path replaces ReconnectAsync.
        }

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

            // Skip retry on ObjectDisposedException: that signals CloseAsync set _disposed
            // mid-create and the just-built connection has already been torn down. Retrying
            // would just re-throw on the next iteration's post-assign disposed check.
            await Retry.DoAsync(
                () => CreateConnectionAsync(cancellationToken),
                async ex =>
                {
                    _logger.LogError(ex, "Error creating connection");
                    await TearDownChannelAndConnectionAsync().ConfigureAwait(false);
                },
                TimeSpan.FromSeconds(_retryTimeInSeconds),
                _retryCount,
                shouldRetry: ex => ex is not ObjectDisposedException,
                cancellationToken).ConfigureAwait(false);

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
        // Set _disposed BEFORE waiting for the semaphore, so a concurrent create can detect
        // it after assignment and tear down its own work rather than orphaning the connection.
        Interlocked.Exchange(ref _disposed, 1);

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
        _connectionFactory = ConnectionFactoryBuilder.Build(_transportConfiguration, _logger);

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

            _lifecycle.Attach(connection);
            // VirtualHost is set on the ConnectionFactory but is not surfaced on
            // AmqpTcpEndpoint. Read it from the transport config — that's the value
            // the factory was built with and what the broker will route against.
            var (host, port) = ConnectionLifecycleHooks.ResolveEndpoint(connection);
            RabbitMqClientLog.ProducerConnectionOpened(
                _logger,
                host,
                port,
                string.IsNullOrEmpty(_transportConfiguration.VirtualHost) ? "/" : _transportConfiguration.VirtualHost,
                connection.ClientProvidedName ?? string.Empty);

            if (_publisherAcks)
            {
                // The producer's primary bound on outstanding confirms is _publishLock = new(1, 1):
                // every publish runs under that single permit, so the RabbitMQ.Client
                // _confirmsTaskCompletionSources dictionary never holds more than one entry at a
                // time even though the upstream library leaves it unbounded by default.
                //
                // The ConcurrencyLimiter installed here is defence-in-depth: RabbitMQ.Client v7.2.1
                // releases the rate-limiter lease BEFORE awaiting the broker confirm
                // (MaybeReleasePublisherConfirmationLock fires before MaybeEndPublisherConfirmationTrackingAsync),
                // so the limiter caps concurrent wire sends, not outstanding-but-unacked confirms.
                // It is a no-op against the current single-permit _publishLock layout, but if a
                // future change ever lets multiple publishes run concurrently against one channel,
                // the upstream tracker would otherwise grow without bound. QueueLimit=int.MaxValue
                // makes overflow back-pressure (queue, then publish) rather than throw.
                var permitLimit = ResolveMaxOutstandingPublishConfirms(_transportConfiguration);
                var rateLimiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
                {
                    PermitLimit = permitLimit,
                    QueueLimit = int.MaxValue,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                });
                // publisherConfirmationsEnabled is also load-bearing for
                // OutboundHeaderBuilder.BuildBasicProperties' aliasing-safety invariant in the
                // SendAsync(Type) fan-out: the broker ack gates the next iteration's
                // re-stamping of baseHeaders. Disabling acks would let RabbitMQ.Client read
                // the alias dict after the next iteration mutates it. See
                // OutboundHeaderBuilder.BuildBasicProperties for the binding contract.
                var channelOptions = new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true,
                    outstandingPublisherConfirmationsRateLimiter: rateLimiter);
                model = await connection.CreateChannelAsync(channelOptions, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                model = await connection.CreateChannelAsync(null, cancellationToken).ConfigureAwait(false);
            }

            _connection = connection;
            _model = model;

            // Race window: CloseAsync may have set _disposed and forced teardown while we were
            // creating. If so, tear down the just-built instances rather than orphaning them.
            if (Volatile.Read(ref _disposed) != 0)
            {
                var orphanModel = Interlocked.Exchange(ref _model, null);
                var orphanConnection = Interlocked.Exchange(ref _connection, null);
                await DisposeModelAsync(orphanModel).ConfigureAwait(false);
                await DisposeConnectionInstanceAsync(orphanConnection).ConfigureAwait(false);
                _connected = false;
                // Null the locals so the outer catch's redundant dispose path is a no-op —
                // the helpers are null-guarded and we have already disposed the references.
                model = null;
                connection = null;
                throw new ObjectDisposedException(nameof(ProducerConnection),
                    "ProducerConnection was disposed while a connection create was in flight; the just-built connection has been torn down.");
            }
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
                // Detach BEFORE close so the broker-driven ConnectionShutdownAsync that fires
                // inside CloseAsync is not re-emitted as a ConnectionLost log entry. Idempotent:
                // a `-=` against an unsubscribed handler is a silent no-op, so the failed-create
                // catch path (which calls into here without ever having attached) is safe.
                _lifecycle.Detach(connection);

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
