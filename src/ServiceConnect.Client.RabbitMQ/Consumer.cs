using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
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
    private readonly ConcurrentBag<Client> _clients = new();
    private readonly bool _durable;
    private readonly int _retryDelay;
    private readonly bool _exclusive;
    private readonly bool _autoDelete;
    private readonly Dictionary<string, object?> _queueArguments;
    private readonly Dictionary<string, object?> _retryQueueArguments;
    private readonly Dictionary<string, object?> _utilityQueueArguments;

    public Consumer(ITransportConfiguration transportConfiguration, IQueueConfiguration queueConfiguration,
        IBusConfiguration busConfiguration, ILogger<Consumer> logger, IServiceConnectConnection? connection = null)
    {
        _transportConfiguration = transportConfiguration;
        _queueConfiguration = queueConfiguration;
        _busConfiguration = busConfiguration;
        _logger = logger;
        _connection = connection;

        var clientSettings = transportConfiguration.ClientSettings;
        _durable = !clientSettings.TryGetValue(RabbitMQSettingKeys.Durable, out var durableVal) || (bool)durableVal;
        _exclusive = clientSettings.TryGetValue(RabbitMQSettingKeys.Exclusive, out var exclusiveVal) && (bool)exclusiveVal;
        _autoDelete = clientSettings.TryGetValue(RabbitMQSettingKeys.AutoDelete, out var autoDeleteVal) && (bool)autoDeleteVal;
        _queueArguments = clientSettings.TryGetValue(RabbitMQSettingKeys.Arguments, out var argsVal) ? (Dictionary<string, object?>)argsVal : [];
        _retryQueueArguments = clientSettings.TryGetValue(RabbitMQSettingKeys.RetryQueueArguments, out var retryArgsVal) ? (Dictionary<string, object?>)retryArgsVal : [];
        _utilityQueueArguments = clientSettings.TryGetValue(RabbitMQSettingKeys.UtilityQueueArguments, out var utilArgsVal) ? (Dictionary<string, object?>)utilArgsVal : [];
        _retryDelay = transportConfiguration.RetryDelay;
    }

    public bool IsConnected => _connection?.IsConnected() ?? false;

    public async Task StartConsumingAsync(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _connection ??= new Connection(_transportConfiguration, queueName, _logger);

        _model ??= await _connection.CreateChannelAsync();

        // Configure exchanges
        foreach (string messageType in messageTypes)
        {
            await ConfigureExchangeAsync(messageType, ExchangeType.Fanout, cancellationToken);
        }

        // Configure queue
        await ConfigureQueueAsync(queueName, cancellationToken);

        // Purge all messages on queue
        if (_queueConfiguration.PurgeQueueOnStartup)
        {
            _logger.LogDebug("Purging queue");
            await _model.QueuePurgeAsync(queueName, cancellationToken);
        }

        // Configure retry queue (but only if retries are expected)
        if (_transportConfiguration.MaxRetries > 0)
        {
            await ConfigureRetryQueueAsync(queueName, cancellationToken);
        }

        // Configure Error Queue/Exchange
        string errorExchangeName = _queueConfiguration.ErrorQueueName;
        await DeclareExchangeAsync(errorExchangeName, ExchangeType.Direct, cancellationToken);
        await DeclareQueueAsync(errorExchangeName, true, _utilityQueueArguments, cancellationToken);

        if (!string.IsNullOrEmpty(errorExchangeName))
        {
            await _model.QueueBindAsync(errorExchangeName, errorExchangeName, string.Empty, _utilityQueueArguments, cancellationToken: cancellationToken);
        }

        // Configure Audit Queue/Exchange
        if (_queueConfiguration.AuditingEnabled)
        {
            string auditQueueName = _queueConfiguration.AuditQueueName;
            await DeclareExchangeAsync(auditQueueName, ExchangeType.Direct, cancellationToken);
            await DeclareQueueAsync(auditQueueName, true, _utilityQueueArguments, cancellationToken);

            if (!string.IsNullOrEmpty(auditQueueName))
            {
                await _model.QueueBindAsync(auditQueueName, auditQueueName, string.Empty, _utilityQueueArguments, cancellationToken: cancellationToken);
            }
        }

        // R-066: Close setup channel — no longer needed after topology is configured.
        if (_model is { IsOpen: true })
            await _model.CloseAsync().ConfigureAwait(false);
        _model?.Dispose();
        _model = null;

        int clientCount = _busConfiguration.ConsumerCount;

        for (int i = 0; i < clientCount; i++)
        {
            Client client = new(_connection, _transportConfiguration, _queueConfiguration, _busConfiguration, _logger);
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
        foreach (Client consumer in _clients)
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

    private async Task ConfigureExchangeAsync(string exchangeName, string type, CancellationToken cancellationToken = default)
    {
        try
        {
            // Hard code auto delete and durable to sensible defaults so that producers and consumers dont try to declare exchanges with different settings.
            await _model!.ExchangeDeclareAsync(exchangeName, type, true, false, null, cancellationToken: cancellationToken);
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogWarning("Error declaring exchange {Message}", ex.Message);
        }
    }

    private async Task ConfigureQueueAsync(string queueName, CancellationToken cancellationToken = default)
    {
        await _model!.QueueDeclareAsync(queueName, _durable, _exclusive, _autoDelete, _queueArguments, cancellationToken: cancellationToken);
    }

    private async Task ConfigureRetryQueueAsync(string queueName, CancellationToken cancellationToken = default)
    {
        // When message goes to retry queue, it falls-through to dead-letter exchange (after _retryDelay)
        // dead-letter exchange is of type "direct" and bound to the original queue.
        string retryQueueName = queueName + RabbitMqQueueNaming.RetryQueueSuffix;
        string retryDeadLetterExchangeName = queueName + RabbitMqQueueNaming.RetryDeadLetterExchangeSuffix;

        try
        {
            await _model!.ExchangeDeclareAsync(retryDeadLetterExchangeName, ExchangeType.Direct, _durable, _autoDelete, null, cancellationToken: cancellationToken);
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogWarning("Error declaring dead letter exchange - {Message}", ex.Message);
        }

        try
        {
            await _model!.QueueBindAsync(queueName, retryDeadLetterExchangeName, retryQueueName, _retryQueueArguments, cancellationToken: cancellationToken); // only redeliver to the original queue (use _queueName as routing key)
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogWarning("Error binding dead letter queue - {Message}", ex.Message);
        }

        Dictionary<string, object?> arguments = new(_retryQueueArguments)
        {
            {RabbitMqQueueNaming.XDeadLetterExchangeArgument, retryDeadLetterExchangeName},
            {RabbitMqQueueNaming.XMessageTtlArgument, _retryDelay}
        };

        try
        {
            // We never have consumers on the retry queue.  Therefore set autodelete to false.
            await _model!.QueueDeclareAsync(retryQueueName, _durable, false, false, arguments, cancellationToken: cancellationToken);
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogWarning("Error declaring queue {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Declares an exchange, logging and swallowing AMQP precondition-failed errors
    /// (the exchange already exists with different settings).
    /// </summary>
    private async Task DeclareExchangeAsync(string name, string type, CancellationToken ct)
    {
        try
        {
            await _model!.ExchangeDeclareAsync(name, type, cancellationToken: ct);
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogWarning("Error declaring exchange {ExchangeName}: {Message}", name, ex.Message);
        }
    }

    /// <summary>
    /// Declares a queue, logging and swallowing AMQP precondition-failed errors
    /// (the queue already exists with different settings).
    /// </summary>
    private async Task DeclareQueueAsync(string name, bool durable, Dictionary<string, object?> arguments, CancellationToken ct)
    {
        try
        {
            await _model!.QueueDeclareAsync(name, durable, false, false, arguments, cancellationToken: ct);
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogWarning("Error declaring queue {QueueName}: {Message}", name, ex.Message);
        }
    }
}
