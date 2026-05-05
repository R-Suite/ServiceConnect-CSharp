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
public sealed class Consumer : IConsumer
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

    /// <summary>
    /// Declares the required RabbitMQ topology and starts consuming messages for the configured queue.
    /// </summary>
    /// <param name="queueName">The queue to consume from.</param>
    /// <param name="messageTypes">The message types whose exchanges should be bound for this consumer.</param>
    /// <param name="eventHandler">The callback invoked when a message is delivered.</param>
    /// <param name="cancellationToken">A token used to cancel startup or consumption initialization.</param>
    public async Task StartConsumingAsync(string queueName, IReadOnlyList<string> messageTypes, ConsumerEventHandler eventHandler, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "Consumer is already consuming. Call DisposeAsync before starting again.");
        }

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
                _transportConfiguration.MaxRetries, _queueConfiguration.ErrorQueueName, _queueConfiguration.QueueName, _logger);
            var auditPublisher = new MessageAuditPublisher(_queueConfiguration);
            RabbitMqConsumerHost client = new(
                _connection,
                _transportConfiguration,
                _queueConfiguration,
                _busConfiguration,
                retryHandler,
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

    /// <summary>
    /// Stops active consumer hosts and releases RabbitMQ resources owned by this instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Each host's DisposeAsync is independently bounded by its own gracefulShutdownTimeout.
        // Sequential disposal made aggregate latency O(N * timeout); parallel makes it O(timeout).
        // Per-host failures (including any OCE — this dispose path is on the Phase 3 fire-and-forget
        // cleanup whitelist) stay isolated via the inner try/catch so a single host's failure cannot
        // short-circuit the rest of the disposal via Task.WhenAll's aggregate-exception path.
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

        // Close and dispose the setup channel before nulling it.
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
