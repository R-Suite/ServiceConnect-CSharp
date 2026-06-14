using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ServiceConnect.Interfaces;
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
    // Track which exchange names have already been declared on the *current* connection.
    // Stamped with the connection generation rather than a bool so a publisher that observed
    // a `true` entry on connection #1 cannot short-circuit re-declare on connection #2 in
    // the window between `_connectionSemaphore` releasing in EnsureConnectedAsync (where
    // _connectionGeneration was bumped and the cache cleared) and the publisher's subsequent
    // ContainsKey check. A stale entry's generation no longer matches `_connectionGeneration`,
    // so the publisher always re-declares on the new channel.
    private readonly ConcurrentDictionary<string, long> _declaredExchanges = new(StringComparer.Ordinal);
    // Monotonic counter — bumped inside the connection semaphore on every successful
    // (re)connect. Read by EnsureExchangeDeclaredAsync to validate cache entries.
    private long _connectionGeneration;
    private readonly SemaphoreSlim _connectionSemaphore = new(1, 1);

    private ConnectionFactory? _connectionFactory;
    private volatile IChannel? _model;
    private volatile IConnection? _connection;
    // ConcurrencyLimiter is the bound on outstanding publisher confirms passed into
    // RabbitMQ.Client's CreateChannelOptions. The driver does not own user-supplied
    // limiters; each connection owns its limiter for its lifetime. Holding the reference
    // here lets TearDown dispose it, and CreateConnectionAsync defensively disposes any
    // predecessor before installing the replacement — preventing a limiter rooted by the
    // closed channel from accumulating unreleased counts across reconnects.
    private RateLimiter? _publisherRateLimiter;
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

    // Test hook consumed by Producer's pass-through property. Setting this on Producer
    // routes through to here so existing test code (`producer.CreateConnectionForTests = ...`)
    // is unchanged.
    internal Func<ConnectionFactory, string[], string, CancellationToken, Task<IConnection>>? CreateConnectionForTests;

    public ProducerConnection(ITransportConfiguration transportConfiguration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(transportConfiguration);
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrEmpty(transportConfiguration.Host))
        {
            throw new ArgumentException("transportConfiguration.Host must be set to a non-empty comma-separated host list.", nameof(transportConfiguration));
        }
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
    /// non-positive values so misconfiguration surfaces loudly. Numeric coercion matches the
    /// convention used elsewhere in this codebase (<c>ConnectionFactoryBuilder.ConvertSettingToInt32</c>,
    /// <c>Convert.ToInt32 / ToInt64 / ToUInt16</c>) so configuration sources that produce
    /// <c>long</c>, <c>string</c>, or other numeric types (e.g. <c>IConfiguration.GetValue</c>,
    /// JSON binders) bind successfully without forcing the caller to cast first.
    /// </summary>
    internal static int ResolveMaxOutstandingPublishConfirms(ITransportConfiguration transport)
    {
        if (!transport.ClientSettings.TryGetValue(RabbitMQSettingKeys.MaxOutstandingPublishConfirms, out var raw))
        {
            return DefaultMaxOutstandingPublishConfirms;
        }
        int permits;
        try
        {
            permits = Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException(
                $"Setting '{RabbitMQSettingKeys.MaxOutstandingPublishConfirms}' must be convertible to Int32; got value '{raw}' of type '{raw?.GetType().FullName ?? "<null>"}'.",
                ex);
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
    /// Tolerant variant of <see cref="Channel"/> for the small TOCTOU window between
    /// <see cref="EnsureConnectedAsync"/> returning healthy and the caller acquiring its
    /// own publish lock — a concurrent reset (slow-path teardown driven by another
    /// publisher's MarkResetRequired flag) can land <c>_model = null</c> in that window.
    /// Returns <see langword="null"/> rather than throwing so the caller can classify
    /// the transient state as retriable without invoking <see cref="MarkResetRequired"/>
    /// again (the concurrent teardown already is the reset).
    /// </summary>
    public IChannel? TryGetChannel() => _model;

    /// <summary>
    /// Returns true only when both the connected flag is set AND the underlying channel
    /// is still open. A broker drop closes the channel without clearing the flag, so
    /// checking the flag alone would permanently suppress reconnect attempts.
    /// </summary>
    public bool IsHealthy() => _connected && (_model?.IsOpen ?? false);

    /// <summary>
    /// Returns an atomic snapshot of the producer's health-relevant state.
    /// Reads <c>_hasAttemptedConnection</c> first; the per-connection invariant is that
    /// <c>_isHealthy=true</c> implies <c>_hasAttemptedConnection=1</c> (set BEFORE the
    /// connection-success branch in <see cref="EnsureConnectedAsync"/>), so observing
    /// <c>_hasAttemptedConnection=0</c> here means a snapshot caller cannot also see
    /// <c>IsHealthy=true</c>. Re-snapshot if the invariant is violated (i.e. the rare
    /// case where a publish raced our two reads).
    /// </summary>
    public ProducerHealthSnapshot GetSnapshot()
    {
        // Read attempted FIRST. If attempted=0, then by the construction of
        // EnsureConnectedAsync (which sets _hasAttemptedConnection=1 before _connected=true)
        // we know IsHealthy()==false at the moment we read attempted=0; observing IsHealthy=true
        // after that read can only happen if we re-read the snapshot, in which case the new
        // attempted read will also be 1.
        var attempted = Volatile.Read(ref _hasAttemptedConnection) != 0;
        var healthy = IsHealthy();

        // Re-snapshot to close the rare double-read race: if we observed attempted=false but
        // healthy=true, that contradicts the invariant — the publish path must have set both
        // between our two reads. Re-read attempted; the new value must be true.
        if (healthy && !attempted)
        {
            attempted = Volatile.Read(ref _hasAttemptedConnection) != 0;
        }

        return new ProducerHealthSnapshot(IsHealthy: healthy, HasAttemptedConnection: attempted);
    }

    /// <summary>
    /// Marks the connection for reset on the next call to <see cref="EnsureConnectedAsync"/>.
    /// Synchronous and idempotent. Used by Producer's publish-timeout and retry paths to
    /// defer the slow teardown+recreate to the next prologue, where it runs under
    /// <c>_connectionSemaphore</c> rather than <c>_publishLock</c>.
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

        // Lock-free fast path: if no reset is pending and the channel is healthy, skip the
        // semaphore entirely. Concurrent publishers all hit this path on the steady-state.
        if (Volatile.Read(ref _resetRequired) == 0 && IsHealthy())
        {
            return;
        }

        // Acquire the semaphore ONCE and hold it across teardown (if reset was required) AND
        // create. Without this, a concurrent publisher's IsHealthy() peek could squeak through
        // between teardown's release and create's re-acquire and observe the stale-but-still-
        // open channel before the new one replaced it.
        await _connectionSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Atomically consume the reset flag inside the semaphore. The whole reset-and-recreate
            // runs under the lock so concurrent peekers see either pre-reset or post-create state,
            // never the in-between half-open window. Also tear down when no reset was marked but
            // the channel is bare-closed (broker-side Channel.Close, queue deletion, mirror failover):
            // without this fall-through the next CreateConnectionAsync would overwrite _connection
            // without disposing the prior reference.
            var resetMarked = Interlocked.Exchange(ref _resetRequired, 0) == 1;
            var needsTeardown = resetMarked || (_connection is not null && !IsHealthy());
            if (needsTeardown)
            {
                await TearDownChannelAndConnectionAsync().ConfigureAwait(false);
                // Bump generation before clearing so a concurrent EnsureExchangeDeclaredAsync
                // that sneaks in between the clear and the subsequent CreateConnectionAsync's
                // own bump cannot stamp an entry under the old generation and fool a later
                // lookup. See CreateConnectionAsync for the full ordering rationale.
                Volatile.Write(ref _connectionGeneration, _connectionGeneration + 1);
                _declaredExchanges.Clear();
            }

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
        // Snapshot the current generation BEFORE the cache lookup so a concurrent reset
        // doesn't make us declare against the new channel and then stamp the cache with
        // a stale generation. Volatile.Read pairs with the Volatile.Write in
        // CreateConnectionAsync to give us an acquire-fence on the generation.
        var generation = Volatile.Read(ref _connectionGeneration);
        if (_declaredExchanges.TryGetValue(exchangeName, out var stamped) && stamped == generation)
        {
            return;
        }

        // _model can be nulled by a concurrent TearDownChannelAndConnectionAsync between
        // the generation snapshot above and this call. Snapshot the channel reference
        // once and check for null so the retriable-publish path classifies this as a
        // transient channel state and retries on the next iteration after reconnect.
        var channel = TryGetChannel()
            ?? throw new ChannelTransientException(
                "Producer channel was torn down concurrently between generation snapshot and exchange declare; retrying.");
        await channel.ExchangeDeclareAsync(exchangeName, type, true, false, null, false, false, cancellationToken).ConfigureAwait(false);
        // Stamp with the generation we observed. If a reset slid in between the snapshot
        // and the declare-call, the next caller's lookup will see generation+1 and won't
        // short-circuit — at worst a redundant re-declare on the new connection, never a
        // declared-on-wrong-channel skip.
        _declaredExchanges[exchangeName] = generation;
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

        // Share a single stopwatch budget across lock wait + broker close. Without this,
        // a stalled broker swallowing close frames hangs the broker-side CloseAsync calls
        // indefinitely after the semaphore wait — same failure shape Connection.cs's R7
        // fix addressed, propagated here so producer and consumer connection-close paths
        // are symmetric.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
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
            // Compute remaining budget for the broker close; floor at 100ms so a fully-
            // exhausted budget still issues a CloseAsync with some chance of success.
            var remaining = timeoutBudget - stopwatch.Elapsed;
            if (remaining < TimeSpan.FromMilliseconds(100))
            {
                remaining = TimeSpan.FromMilliseconds(100);
            }

            // Best-effort teardown ALWAYS runs, whether or not we held the lock.
            try { await TearDownChannelAndConnectionAsync(remaining).ConfigureAwait(false); }
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

        // Exchange declarations are per-connection. Bump the generation FIRST, then clear:
        // a concurrent EnsureExchangeDeclaredAsync that read the old generation sees an
        // entry stamped with that generation and short-circuits — but its declare was made
        // against the prior channel, which is the channel its publish will use, so the
        // skip is safe. A caller that arrives AFTER the bump reads the new generation
        // and any leftover stale entry no longer matches, forcing a fresh declare on the
        // new channel. Volatile.Write provides release-fence ordering with the matching
        // Volatile.Read in EnsureExchangeDeclaredAsync.
        Volatile.Write(ref _connectionGeneration, _connectionGeneration + 1);
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
                // The ConcurrencyLimiter installed here is defence-in-depth: RabbitMQ.Client
                // releases the rate-limiter lease BEFORE awaiting the broker confirm
                // (MaybeReleasePublisherConfirmationLock fires before MaybeEndPublisherConfirmationTrackingAsync),
                // so the limiter caps concurrent wire sends, not outstanding-but-unacked confirms.
                // It is a no-op against the current single-permit _publishLock layout, but if a
                // future change ever lets multiple publishes run concurrently against one channel,
                // the upstream tracker would otherwise grow without bound. QueueLimit=int.MaxValue
                // makes overflow back-pressure (queue, then publish) rather than throw.
                var permitLimit = ResolveMaxOutstandingPublishConfirms(_transportConfiguration);
                // Defensive: a previous reconnect's limiter must be disposed before the
                // new one is installed. TearDownChannelAndConnectionAsync disposes it on
                // every reset, so this is normally null on the create-from-scratch path;
                // the swap is here for the case where CreateConnectionAsync is reached
                // without an intervening TearDown.
                var prior = Interlocked.Exchange(ref _publisherRateLimiter, null);
                if (prior is not null)
                {
                    await prior.DisposeAsync().ConfigureAwait(false);
                }
                var rateLimiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
                {
                    PermitLimit = permitLimit,
                    QueueLimit = int.MaxValue,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                });
                _publisherRateLimiter = rateLimiter;
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
                var orphanLimiter = Interlocked.Exchange(ref _publisherRateLimiter, null);
                await DisposeModelAsync(orphanModel).ConfigureAwait(false);
                await DisposeConnectionInstanceAsync(orphanConnection).ConfigureAwait(false);
                if (orphanLimiter is not null)
                {
                    try { await orphanLimiter.DisposeAsync().ConfigureAwait(false); }
                    catch (ObjectDisposedException) { }
                }
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
            // CreateChannelAsync may have thrown after _publisherRateLimiter was assigned;
            // dispose to avoid leaking on the failed-create path. Use Exchange so a
            // subsequent successful retry can install a fresh limiter without observing
            // a stale field.
            var limiter = Interlocked.Exchange(ref _publisherRateLimiter, null);
            if (limiter is not null)
            {
                try { await limiter.DisposeAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { }
            }
            throw;
        }
    }

    private async Task TearDownChannelAndConnectionAsync(TimeSpan? closeTimeout = null)
    {
        var model = Interlocked.Exchange(ref _model, null);
        var connection = Interlocked.Exchange(ref _connection, null);
        var rateLimiter = Interlocked.Exchange(ref _publisherRateLimiter, null);

        await DisposeModelAsync(model, closeTimeout).ConfigureAwait(false);
        await DisposeConnectionInstanceAsync(connection, closeTimeout).ConfigureAwait(false);
        // Dispose the rate limiter AFTER the channel is gone: any publish in flight
        // has already errored out on the closed channel, so no caller is still
        // waiting on a permit when the limiter dispose invalidates outstanding
        // leases. Disposal is best-effort — a transient ObjectDisposedException
        // from a torn-down concurrent caller is the documented limiter shutdown
        // behaviour and must not propagate out of teardown.
        if (rateLimiter is not null)
        {
            try
            {
                await rateLimiter.DisposeAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { }
        }
        _connected = false;
    }

    private async Task DisposeModelAsync(IChannel? model, TimeSpan? closeTimeout = null)
    {
        if (model != null)
        {
            try
            {
                _logger.LogDebug("Disposing Model");
                if (model.IsOpen)
                {
                    if (closeTimeout is { } budget)
                    {
                        // Bound the broker close so a stalled broker swallowing close frames
                        // cannot wedge dispose past the caller's budget. Mirrors Connection.cs's
                        // R7 fix on the consumer side.
                        using var closeCts = new CancellationTokenSource(budget);
                        try { await model.CloseAsync(closeCts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (closeCts.IsCancellationRequested)
                        {
                            _logger.LogWarning("Model close timed out within {Budget}; proceeding with disposal.", budget);
                        }
                    }
                    else
                    {
                        await model.CloseAsync().ConfigureAwait(false);
                    }
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

    private async Task DisposeConnectionInstanceAsync(IConnection? connection, TimeSpan? closeTimeout = null)
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
                    if (closeTimeout is { } budget)
                    {
                        using var closeCts = new CancellationTokenSource(budget);
                        try { await connection.CloseAsync(closeCts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (closeCts.IsCancellationRequested)
                        {
                            _logger.LogWarning("Connection close timed out within {Budget}; proceeding with disposal.", budget);
                        }
                    }
                    else
                    {
                        await connection.CloseAsync().ConfigureAwait(false);
                    }
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
