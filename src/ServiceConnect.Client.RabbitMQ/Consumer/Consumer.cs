using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// RabbitMQ-backed implementation of <see cref="IConsumer"/> for ServiceConnect.
/// </summary>
internal sealed class Consumer : IConsumer
{
    private IChannel? _model;
    private IServiceConnectConnection? _connection;
    // True only when this Consumer created the connection. A connection supplied via
    // the constructor is caller-owned and must NOT be disposed here — disposing it
    // would tear down whatever else the caller is using it for (Producer, other Consumers).
    private bool _ownsConnection;
    private readonly ILogger<Consumer> _logger;
    private readonly ITransportConfiguration _transportConfiguration;
    private readonly IQueueConfiguration _queueConfiguration;
    private readonly IBusConfiguration _busConfiguration;
    private readonly ConcurrentBag<IAsyncDisposable> _clients = [];
    private int _started; // 0 = not started, 1 = started; access only via Interlocked
    private int _stopped; // 0 = active, 1 = stopped or disposed; latched for IsStopped readers
    // Startup paths (StartConsumingAsync) acquire this. DisposeAsync uses a SEPARATE
    // semaphore so a wedged startup cannot block SIGTERM shutdown — they touch
    // different state (startup builds the _hosts ConcurrentBag; dispose drains it).
    // The ConcurrentBag handles the concurrent producer/consumer pattern correctly.
    private readonly SemaphoreSlim _startupSemaphore = new(1, 1);

    // DisposeAsync acquires this. Bounded by BusConfiguration.DisposeTimeout so a
    // wedged dispose path (e.g. broker handshake stuck) eventually surfaces rather
    // than blocking process exit.
    private readonly SemaphoreSlim _disposeSemaphore = new(1, 1);
    private readonly bool _durable;
    private readonly int _retryDelay;
    private readonly bool _exclusive;
    private readonly bool _autoDelete;
    private readonly Dictionary<string, object?> _queueArguments;
    private readonly Dictionary<string, object?> _retryQueueArguments;
    private readonly Dictionary<string, object?> _utilityQueueArguments;
    private readonly RabbitMqTopologyProvisioner _topologyProvisioner;

    /// <summary>
    /// Initializes a new consumer instance using the supplied ServiceConnect configuration.
    /// </summary>
    /// <param name="transportConfiguration">Transport settings used to configure RabbitMQ connectivity and retry behavior.</param>
    /// <param name="queueConfiguration">Queue settings used for queue names, auditing, and purge behavior.</param>
    /// <param name="busConfiguration">Bus settings that control consumer concurrency.</param>
    /// <param name="logger">The logger used for consumer lifecycle and provisioning messages.</param>
    /// <param name="connection">An optional connection to reuse instead of creating a new one.</param>
    public Consumer(ITransportConfiguration transportConfiguration, IQueueConfiguration queueConfiguration,
        IBusConfiguration busConfiguration, ILogger<Consumer> logger, IServiceConnectConnection? connection = null)
    {
        _transportConfiguration = transportConfiguration;
        _queueConfiguration = queueConfiguration;
        _busConfiguration = busConfiguration;
        _logger = logger;
        _connection = connection;
        _ownsConnection = connection is null;

        // Move configuration extraction to the constructor, making fields readonly.
        var clientSettings = transportConfiguration.ClientSettings;
        _durable = !clientSettings.TryGetValue(RabbitMQSettingKeys.Durable, out var durableVal) || (bool)durableVal;
        _exclusive = clientSettings.TryGetValue(RabbitMQSettingKeys.Exclusive, out var exclusiveVal) && (bool)exclusiveVal;
        _autoDelete = clientSettings.TryGetValue(RabbitMQSettingKeys.AutoDelete, out var autoDeleteVal) && (bool)autoDeleteVal;
        _queueArguments = CoerceToQueueArgs(clientSettings, RabbitMQSettingKeys.Arguments);
        _retryQueueArguments = CoerceToQueueArgs(clientSettings, RabbitMQSettingKeys.RetryQueueArguments);
        _utilityQueueArguments = CoerceToQueueArgs(clientSettings, RabbitMQSettingKeys.UtilityQueueArguments);
        _retryDelay = transportConfiguration.RetryDelay;

        // Create the topology provisioner once.
        _topologyProvisioner = new RabbitMqTopologyProvisioner(logger);
    }

    /// <summary>
    /// Gets a value indicating whether the consumer currently has an open RabbitMQ connection.
    /// </summary>
    public bool IsConnected => _connection?.IsConnected() ?? false;

    /// <summary>
    /// Gets a value indicating whether the broker has cancelled at least one of our hosts'
    /// consumers (queue deleted, policy expired, mirror promoted). The Bus surfaces this via
    /// <see cref="IBus.IsConsuming"/> = false so <c>BusConsumingHealthCheck</c> reports Unhealthy.
    /// </summary>
    public bool IsCancelledByBroker => _clients.OfType<RabbitMqConsumerHost>().Any(c => c.IsCancelledByBroker);

    /// <inheritdoc />
    public bool IsStopped => Volatile.Read(ref _stopped) != 0;

    /// <summary>
    /// Declares the required RabbitMQ topology and starts consuming messages for the configured queue.
    /// </summary>
    /// <param name="queueName">The queue to consume from.</param>
    /// <param name="messageTypes">The message types whose exchanges should be bound for this consumer.</param>
    /// <param name="eventHandler">The callback invoked when a message is delivered.</param>
    /// <param name="cancellationToken">A token used to cancel startup or consumption initialization.</param>
    public async Task StartConsumingAsync(string queueName, IReadOnlyList<string> messageTypes, ConsumerEventHandler eventHandler, CancellationToken cancellationToken = default)
    {
        // Reject a zero or negative ConsumerCount before acquiring start state. When this
        // consumer is constructed directly (bypassing the builder), the builder's validator
        // does not run, so the guard here is the last line of defence against a
        // misconfiguration that would silently skip the client-construction loop and leave
        // the bus consuming nothing.
        if (_busConfiguration.ConsumerCount < 1)
        {
            throw new InvalidOperationException(
                $"BusConfiguration.ConsumerCount must be at least 1 (got {_busConfiguration.ConsumerCount}).");
        }

        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "Consumer is already consuming. Call DisposeAsync before starting again.");
        }

        // Clear the stopped latch — DisposeAsync sets it for IsStopped readers, and a
        // DisposeAsync → StartConsumingAsync cycle (supported by the _started reset in
        // DisposeAsync) must report the freshly-started consumer as not-stopped.
        Interlocked.Exchange(ref _stopped, 0);

        // Acquire the startup lock for the duration of setup. Serialises concurrent
        // StartConsumingAsync calls so only one sets up the channel and _hosts bag at
        // a time. Honour the caller's cancellation token directly; this lock is
        // independent of DisposeAsync so a dispose cannot block entry here.
        await _startupSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        var lifecycleHeld = true;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_connection is null)
            {
                _connection = new Connection(_transportConfiguration, queueName, _logger);
                _ownsConnection = true;
            }
            IChannel? setupChannel = null;
            try
            {
                setupChannel = await _connection.CreateChannelAsync(cancellationToken).ConfigureAwait(false);
                _model = setupChannel;

                // Mark as initial setup for re-throwing on first topology setup.
                const bool isInitialSetup = true;

                // Configure exchanges
                foreach (string messageType in messageTypes)
                {
                    await _topologyProvisioner.ConfigureDeclareExchangeAsync(_model, messageType, ExchangeType.Fanout, isInitialSetup, cancellationToken).ConfigureAwait(false);
                }

                // Configure queue
                await _topologyProvisioner.ConfigureDeclareQueueAsync(
                    _model,
                    queueName,
                    _durable,
                    _exclusive,
                    _autoDelete,
                    _queueArguments,
                    isInitialSetup,
                    cancellationToken).ConfigureAwait(false);

                // Purge all messages on queue
                if (_queueConfiguration.PurgeQueueOnStartup)
                {
                    _logger.LogDebug("Purging queue");
                    await _model.QueuePurgeAsync(queueName, cancellationToken).ConfigureAwait(false);
                }

                // Configure retry queue (but only if retries are expected)
                if (_transportConfiguration.MaxRetries > 0)
                {
                    await _topologyProvisioner.ConfigureRetryTopologyAsync(
                        _model, queueName, _durable, _autoDelete, _retryDelay,
                        _retryQueueArguments, isInitialSetup, cancellationToken).ConfigureAwait(false);
                }

                // Use the provisioner for utility queue setup.
                string errorExchangeName = _queueConfiguration.ErrorQueueName;
                await _topologyProvisioner.ConfigureDeclareUtilityQueueAsync(_model, errorExchangeName, _utilityQueueArguments, isInitialSetup, cancellationToken).ConfigureAwait(false);

                // Configure Audit Queue/Exchange
                if (_queueConfiguration.AuditingEnabled)
                {
                    string auditQueueName = _queueConfiguration.AuditQueueName;
                    await _topologyProvisioner.ConfigureDeclareUtilityQueueAsync(_model, auditQueueName, _utilityQueueArguments, isInitialSetup, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                // Always close the setup channel once topology provisioning completes or fails.
                if (setupChannel is { IsOpen: true })
                {
                    await setupChannel.CloseAsync().ConfigureAwait(false);
                }

                setupChannel?.Dispose();
                if (ReferenceEquals(_model, setupChannel))
                {
                    _model = null;
                }
            }

            int clientCount = _busConfiguration.ConsumerCount;

            for (int i = 0; i < clientCount; i++)
            {
                var retryHandler = new MessageRetryHandler(
                    _transportConfiguration.MaxRetries,
                    _queueConfiguration.ErrorQueueName,
                    _queueConfiguration.QueueName,
                    _logger,
                    timeProvider: null,
                    errorsDisabled: _queueConfiguration.DisableErrors);
                var auditPublisher = new MessageAuditPublisher(_queueConfiguration);
                var admissionGate = new RabbitMqAdmissionGate(_queueConfiguration.QueueName);
                RabbitMqConsumerHost client = new(
                    _connection,
                    _transportConfiguration,
                    _queueConfiguration,
                    _busConfiguration,
                    retryHandler,
                    admissionGate,
                    auditPublisher,
                    _logger);
                // Register the host before starting so a failure in PrepareAsync or
                // ConsumeMessageTypeAsync on a later iteration does not leak already-started
                // hosts. DisposeAsync iterates _clients and tolerates half-started hosts.
                _clients.Add(client);
                await client.PrepareAsync(eventHandler, queueName, cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (string messageType in messageTypes)
                {
                    await client.ConsumeMessageTypeAsync(messageType, cancellationToken).ConfigureAwait(false);
                }
                await client.BeginConsumingAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Reset _started so a subsequent StartConsumingAsync can retry; without this, a
            // failure mid-setup leaves the consumer in a half-built "already consuming" state
            // requiring an explicit DisposeAsync to recover.
            Interlocked.Exchange(ref _started, 0);

            // Snapshot and detach the owned connection BEFORE releasing the lifecycle
            // semaphore (see RecoverStartFailureAsync for the race rationale).
            IServiceConnectConnection? connectionToDispose = null;
            if (_ownsConnection && _connection != null)
            {
                connectionToDispose = _connection;
                _connection = null;
                _ownsConnection = false;
            }

            if (lifecycleHeld)
            {
                _startupSemaphore.Release();
                lifecycleHeld = false;
            }

            await RecoverStartFailureAsync(connectionToDispose).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (lifecycleHeld)
            {
                _startupSemaphore.Release();
            }
        }
    }

    // Recovery cleanup for StartConsumingAsync's catch path. Drains _clients (in case the
    // for-loop added hosts before the failure) and disposes the owned connection captured
    // before the lifecycle semaphore was released. Idempotent against Consumer.DisposeAsync's
    // own _clients.Clear() / connection dispose — both use TryTake and IAsyncDisposable
    // patterns that tolerate the second invocation.
    private async Task RecoverStartFailureAsync(IServiceConnectConnection? connectionToDispose)
    {
        while (_clients.TryTake(out var partial))
        {
            try { await partial.DisposeAsync().ConfigureAwait(false); }
            catch (Exception disposeEx) { _logger.LogWarning(disposeEx, "Error disposing partial host during StartConsumingAsync failure recovery"); }
        }
        if (connectionToDispose is not null)
        {
            try { await connectionToDispose.DisposeAsync().ConfigureAwait(false); }
            catch (Exception connEx) { _logger.LogWarning(connEx, "Error disposing owned connection during StartConsumingAsync failure recovery"); }
        }
    }

    /// <summary>
    /// Issues a graceful BasicCancel to every consumer host so the broker stops delivering,
    /// then waits for in-flight handler invocations to drain. Does not tear down the
    /// channel/connection — that happens on <see cref="DisposeAsync"/>. Idempotent.
    /// </summary>
    public async Task StopConsumingAsync(CancellationToken cancellationToken = default)
    {
        // Latch IsStopped on entry so a probe firing during the drain reports the consumer
        // as permanently stopped rather than waiting out the recovery-grace window.
        Interlocked.Exchange(ref _stopped, 1);

        // Stop in parallel so aggregate latency is O(graceful-shutdown-timeout) rather than
        // O(N * timeout). Per-host failures stay isolated via the inner try/catch so a
        // single host's error cannot short-circuit the rest via Task.WhenAll's aggregate-
        // exception path.
        var stopTasks = _clients
            .OfType<RabbitMqConsumerHost>()
            .Select(async host =>
            {
                try
                {
                    await host.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error stopping consumer host - continuing");
                }
            })
            .ToArray();

        await Task.WhenAll(stopTasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops active consumer hosts and releases RabbitMQ resources owned by this instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Latch IsStopped first so a health probe firing during disposal reports the
        // consumer as permanently stopped rather than waiting out the recovery-grace window.
        Interlocked.Exchange(ref _stopped, 1);

        // Bounded by DisposeTimeout so a wedged dispose itself cannot hang container shutdown.
        // The semaphore is dedicated to DisposeAsync; a still-running StartConsumingAsync
        // acquires its own _startupSemaphore and runs concurrently with dispose. This is
        // intentional — the pre-split single-semaphore design had StartConsumingAsync
        // blocking SIGTERM when a topology-provision retry loop held the lock.
        var lifecycleTimeout = _busConfiguration.DisposeTimeout > TimeSpan.Zero
            ? _busConfiguration.DisposeTimeout
            : TimeSpan.FromSeconds(30);
        var lifecycleAcquired = await _disposeSemaphore.WaitAsync(lifecycleTimeout).ConfigureAwait(false);
        if (!lifecycleAcquired)
        {
            _logger.LogWarning(
                "Consumer.DisposeAsync timed out waiting to acquire the dispose semaphore after {Timeout}. " +
                "A concurrent DisposeAsync may still be running. _model and _connection are not torn down; " +
                "_started is left set so a subsequent StartConsumingAsync fails fast. " +
                "Resolve the wedge (process restart) before resuming consumption.",
                lifecycleTimeout);
        }

        // Each host's DisposeAsync is independently bounded by its own gracefulShutdownTimeout.
        // Sequential disposal made aggregate latency O(N * timeout); parallel makes it O(timeout).
        // Per-host failures (including any OCE — this dispose path is fire-and-forget cleanup)
        // stay isolated via the inner try/catch so a single host's failure cannot short-circuit
        // the rest of the disposal via Task.WhenAll's aggregate-exception path.
        var disposeTasks = _clients
            .Select(async consumer =>
            {
                try
                {
                    await consumer.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to dispose consumer host - continuing");
                }
            })
            .ToArray();

        await Task.WhenAll(disposeTasks).ConfigureAwait(false);
        // Reset the bag so a subsequent StartConsumingAsync starts from empty;
        // otherwise per-cycle entries accumulate and the disposed-host references
        // are retained for the lifetime of the Consumer.
        _clients.Clear();

        // Only tear down _model and _connection when this DisposeAsync holds the semaphore.
        // Without the guard, two concurrent DisposeAsync calls could both attempt teardown
        // simultaneously, producing opaque AlreadyClosedException or double-dispose faults.
        if (lifecycleAcquired)
        {
            if (_model is { IsOpen: true })
            {
                try { await _model.CloseAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Error closing consumer setup channel"); }
            }
            _model?.Dispose();
            _model = null;
            if (_ownsConnection && _connection != null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                // Null the field so a subsequent StartConsumingAsync recreates the
                // connection rather than handing back a disposed one. _ownsConnection
                // is set fresh on the next StartConsumingAsync, so it does not need a
                // matching reset here.
                _connection = null;
            }

            // Reset the started flag so a DisposeAsync → StartConsumingAsync sequence remains valid.
            Interlocked.Exchange(ref _started, 0);

            // The semaphore is intentionally NOT disposed: a concurrent late-arriving
            // dispose call would otherwise observe ObjectDisposedException out of
            // WaitAsync. Leaving it un-disposed costs only the un-allocated lazy
            // WaitHandle (we never call AvailableWaitHandle) which is reclaimed with the
            // Consumer instance.
            _disposeSemaphore.Release();
        }
        // When lifecycleAcquired is false a wedged DisposeAsync path held the semaphore
        // beyond the timeout. _started is left at 1 so a subsequent StartConsumingAsync
        // fails fast with InvalidOperationException ("already consuming") rather than
        // silently building duplicate state. Operators must resolve the wedge (typically
        // process restart) before consumption resumes.
    }

    private static Dictionary<string, object?> CoerceToQueueArgs(IReadOnlyDictionary<string, object> settings, string key)
    {
        // Accept any dictionary-shaped value; copy into a plain Dictionary<,> so downstream
        // mutation and enumeration operate on a concrete, non-read-only instance. Direct casting
        // to Dictionary<,> broke for callers using ReadOnlyDictionary / SortedDictionary /
        // ImmutableDictionary.
        if (!settings.TryGetValue(key, out var raw) || raw is null)
        {
            return [];
        }

        return raw switch
        {
            Dictionary<string, object?> d => d,
            IDictionary<string, object?> id => new Dictionary<string, object?>(id, StringComparer.Ordinal),
            IReadOnlyDictionary<string, object?> rd => rd.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            _ => throw new InvalidOperationException(
                $"Setting '{key}' must be IDictionary<string, object?> or IReadOnlyDictionary<string, object?>; got {raw.GetType().FullName}."),
        };
    }
}
