using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using System.Collections.Concurrent;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// RabbitMQ-backed implementation of <see cref="IConsumer"/> for ServiceConnect.
/// </summary>
public sealed class Consumer : IConsumer
{
    private IChannel? _model;
    private IServiceConnectConnection? _connection;
    private readonly ILogger<Consumer> _logger;
    private readonly ITransportConfiguration _transportConfiguration;
    private readonly IQueueConfiguration _queueConfiguration;
    private readonly IBusConfiguration _busConfiguration;
    private readonly ConcurrentBag<RabbitMqConsumerHost> _clients = new();
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

        // Move configuration extraction to the constructor, making fields readonly.
        var clientSettings = transportConfiguration.ClientSettings;
        _durable = !clientSettings.TryGetValue(RabbitMQSettingKeys.Durable, out var durableVal) || (bool)durableVal;
        _exclusive = clientSettings.TryGetValue(RabbitMQSettingKeys.Exclusive, out var exclusiveVal) && (bool)exclusiveVal;
        _autoDelete = clientSettings.TryGetValue(RabbitMQSettingKeys.AutoDelete, out var autoDeleteVal) && (bool)autoDeleteVal;
        _queueArguments = clientSettings.TryGetValue(RabbitMQSettingKeys.Arguments, out var argsVal) ? (Dictionary<string, object?>)argsVal : [];
        _retryQueueArguments = clientSettings.TryGetValue(RabbitMQSettingKeys.RetryQueueArguments, out var retryArgsVal) ? (Dictionary<string, object?>)retryArgsVal : [];
        _utilityQueueArguments = clientSettings.TryGetValue(RabbitMQSettingKeys.UtilityQueueArguments, out var utilArgsVal) ? (Dictionary<string, object?>)utilArgsVal : [];
        _retryDelay = transportConfiguration.RetryDelay;

        // Create the topology provisioner once.
        _topologyProvisioner = new RabbitMqTopologyProvisioner(logger);
    }

    /// <summary>
    /// Gets a value indicating whether the consumer currently has an open RabbitMQ connection.
    /// </summary>
    public bool IsConnected => _connection?.IsConnected() ?? false;

    /// <summary>
    /// Declares the required RabbitMQ topology and starts consuming messages for the configured queue.
    /// </summary>
    /// <param name="queueName">The queue to consume from.</param>
    /// <param name="messageTypes">The message types whose exchanges should be bound for this consumer.</param>
    /// <param name="eventHandler">The callback invoked when a message is delivered.</param>
    /// <param name="cancellationToken">A token used to cancel startup or consumption initialization.</param>
    public async Task StartConsumingAsync(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _connection ??= new Connection(_transportConfiguration, queueName, _logger);
        IChannel? setupChannel = null;
        try
        {
            setupChannel = await _connection.CreateChannelAsync();
            _model = setupChannel;

            // Mark as initial setup for re-throwing on first topology setup.
            const bool isInitialSetup = true;

            // Configure exchanges
            foreach (string messageType in messageTypes)
            {
                await _topologyProvisioner.ConfigureDeclareExchangeAsync(_model, messageType, ExchangeType.Fanout, isInitialSetup, cancellationToken);
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
                cancellationToken);

            // Purge all messages on queue
            if (_queueConfiguration.PurgeQueueOnStartup)
            {
                _logger.LogDebug("Purging queue");
                await _model.QueuePurgeAsync(queueName, cancellationToken);
            }

            // Configure retry queue (but only if retries are expected)
            if (_transportConfiguration.MaxRetries > 0)
            {
                await _topologyProvisioner.ConfigureRetryTopologyAsync(
                    _model, queueName, _durable, _autoDelete, _retryDelay,
                    _retryQueueArguments, isInitialSetup, cancellationToken);
            }

            // Use the provisioner for utility queue setup.
            string errorExchangeName = _queueConfiguration.ErrorQueueName;
            await _topologyProvisioner.ConfigureDeclareUtilityQueueAsync(_model, errorExchangeName, _utilityQueueArguments, isInitialSetup, cancellationToken);

            // Configure Audit Queue/Exchange
            if (_queueConfiguration.AuditingEnabled)
            {
                string auditQueueName = _queueConfiguration.AuditQueueName;
                await _topologyProvisioner.ConfigureDeclareUtilityQueueAsync(_model, auditQueueName, _utilityQueueArguments, isInitialSetup, cancellationToken);
            }
        }
        finally
        {
            // Always close the setup channel once topology provisioning completes or fails.
            if (setupChannel is { IsOpen: true })
                await setupChannel.CloseAsync().ConfigureAwait(false);
            setupChannel?.Dispose();
            if (ReferenceEquals(_model, setupChannel))
                _model = null;
        }

        int clientCount = _busConfiguration.ConsumerCount;

        for (int i = 0; i < clientCount; i++)
        {
            var retryHandler = new MessageRetryHandler(
                _transportConfiguration.MaxRetries, _queueConfiguration.ErrorQueueName, _logger);
            var auditPublisher = new MessageAuditPublisher(_queueConfiguration);
            RabbitMqConsumerHost client = new(
                _connection,
                _transportConfiguration,
                _queueConfiguration,
                _busConfiguration,
                retryHandler,
                auditPublisher,
                _logger);
            await client.StartConsumingAsync(eventHandler, queueName, cancellationToken: cancellationToken);
            foreach (string messageType in messageTypes)
            {
                await client.ConsumeMessageTypeAsync(messageType);
            }
            _clients.Add(client);
        }
    }

    /// <summary>
    /// Stops active consumer hosts and releases RabbitMQ resources owned by this instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (RabbitMqConsumerHost consumer in _clients)
        {
            try { await consumer.DisposeAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }

        // Close and dispose the setup channel before nulling it.
        if (_model is { IsOpen: true })
        {
            try { await _model.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error closing consumer setup channel"); }
        }
        _model?.Dispose();
        _model = null;
        if (_connection != null)
            await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
