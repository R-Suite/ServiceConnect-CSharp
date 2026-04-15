using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using System.Collections.Concurrent;

namespace ServiceConnect.Client.RabbitMQ;

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

    public Consumer(ITransportConfiguration transportConfiguration, IQueueConfiguration queueConfiguration,
        IBusConfiguration busConfiguration, ILogger<Consumer> logger, IServiceConnectConnection? connection = null)
    {
        _transportConfiguration = transportConfiguration;
        _queueConfiguration = queueConfiguration;
        _busConfiguration = busConfiguration;
        _logger = logger;
        _connection = connection;

        // R-043: Move configuration extraction to constructor, making fields readonly
        var clientSettings = transportConfiguration.ClientSettings;
        _durable = !clientSettings.TryGetValue(RabbitMQSettingKeys.Durable, out var durableVal) || (bool)durableVal;
        _exclusive = clientSettings.TryGetValue(RabbitMQSettingKeys.Exclusive, out var exclusiveVal) && (bool)exclusiveVal;
        _autoDelete = clientSettings.TryGetValue(RabbitMQSettingKeys.AutoDelete, out var autoDeleteVal) && (bool)autoDeleteVal;
        _queueArguments = clientSettings.TryGetValue(RabbitMQSettingKeys.Arguments, out var argsVal) ? (Dictionary<string, object?>)argsVal : [];
        _retryQueueArguments = clientSettings.TryGetValue(RabbitMQSettingKeys.RetryQueueArguments, out var retryArgsVal) ? (Dictionary<string, object?>)retryArgsVal : [];
        _utilityQueueArguments = clientSettings.TryGetValue(RabbitMQSettingKeys.UtilityQueueArguments, out var utilArgsVal) ? (Dictionary<string, object?>)utilArgsVal : [];
        _retryDelay = transportConfiguration.RetryDelay;

        // R-010: Create topology provisioner
        _topologyProvisioner = new RabbitMqTopologyProvisioner(logger);
    }

    public bool IsConnected => _connection?.IsConnected() ?? false;

    public async Task StartConsumingAsync(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _connection ??= new Connection(_transportConfiguration, queueName, _logger);
        IChannel? setupChannel = null;
        try
        {
            setupChannel = await _connection.CreateChannelAsync();
            _model = setupChannel;

            // R-032: Mark as initial setup for re-throwing on first topology setup
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

            // R-070: Use provisioner for utility queue setup
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
            // R-066: always close the setup channel once topology provisioning completes or fails.
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

    public async ValueTask DisposeAsync()
    {
        foreach (RabbitMqConsumerHost consumer in _clients)
        {
            try { await consumer.DisposeAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }

        // Close and dispose the setup channel before nulling (R-012).
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
