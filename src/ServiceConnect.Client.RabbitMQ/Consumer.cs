using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using System.Collections.Concurrent;

namespace ServiceConnect.Client.RabbitMQ;

public sealed class Consumer : IConsumer
{
    private IChannel? _model;
    private bool _durable;
    private int _retryDelay;
    private bool _exclusive;
    private bool _autoDelete;
    private IServiceConnectConnection? _connection;
    private readonly ILogger<Consumer> _logger;
    private readonly ITransportConfiguration _transportConfiguration;
    private readonly IQueueConfiguration _queueConfiguration;
    private Dictionary<string, object?> _queueArguments = [];
    private Dictionary<string, object?> _retryQueueArguments = [];
    private Dictionary<string, object?> _utilityQueueArguments = [];
    private readonly ConcurrentBag<Client> _clients = new();
    private readonly IBusConfiguration _busConfiguration;
    private CancellationToken _consumingCt;

    public Consumer(ITransportConfiguration transportConfiguration, IQueueConfiguration queueConfiguration, IBusConfiguration busConfiguration, ILogger<Consumer> logger)
    {
        _transportConfiguration = transportConfiguration;
        _queueConfiguration = queueConfiguration;
        _busConfiguration = busConfiguration;
        _logger = logger;
    }

    public Consumer(IServiceConnectConnection connection, ITransportConfiguration transportConfiguration, IQueueConfiguration queueConfiguration, IBusConfiguration busConfiguration, ILogger<Consumer> logger)
    {
        _connection = connection;
        _transportConfiguration = transportConfiguration;
        _queueConfiguration = queueConfiguration;
        _busConfiguration = busConfiguration;
        _logger = logger;
    }

    public bool IsConnected => _connection?.IsConnected() ?? false;

    public async Task StartConsumingAsync(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _consumingCt = cancellationToken;

        var clientSettings = _transportConfiguration.ClientSettings;

        _durable = !clientSettings.TryGetValue(RabbitMQSettingKeys.Durable, out var durableVal) || (bool)durableVal;
        _exclusive = clientSettings.TryGetValue(RabbitMQSettingKeys.Exclusive, out var exclusiveVal) && (bool)exclusiveVal;
        _autoDelete = clientSettings.TryGetValue(RabbitMQSettingKeys.AutoDelete, out var autoDeleteVal) && (bool)autoDeleteVal;
        _queueArguments = clientSettings.TryGetValue(RabbitMQSettingKeys.Arguments, out var argsVal) ? (Dictionary<string, object?>)argsVal : [];
        _retryQueueArguments = clientSettings.TryGetValue(RabbitMQSettingKeys.RetryQueueArguments, out var retryArgsVal) ? (Dictionary<string, object?>)retryArgsVal : [];
        _utilityQueueArguments = clientSettings.TryGetValue(RabbitMQSettingKeys.UtilityQueueArguments, out var utilArgsVal) ? (Dictionary<string, object?>)utilArgsVal : [];
        _retryDelay = _transportConfiguration.RetryDelay;

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
        string errorExchange = await ConfigureErrorExchangeAsync(cancellationToken);
        string errorQueue = await ConfigureErrorQueueAsync(cancellationToken);

        if (!string.IsNullOrEmpty(errorExchange))
        {
            await _model.QueueBindAsync(errorQueue, errorExchange, string.Empty, _utilityQueueArguments, cancellationToken: cancellationToken);
        }

        // Configure Audit Queue/Exchange
        if (_queueConfiguration.AuditingEnabled)
        {
            string auditExchange = await ConfigureAuditExchangeAsync(cancellationToken);
            string auditQueue = await ConfigureAuditQueueAsync(cancellationToken);

            if (!string.IsNullOrEmpty(auditExchange))
            {
                await _model.QueueBindAsync(auditQueue, auditExchange, string.Empty, _utilityQueueArguments, cancellationToken: cancellationToken);
            }
        }

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
        catch (Exception ex)
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
        catch (Exception ex)
        {
            _logger.LogWarning("Error declaring dead letter exchange - {Message}", ex.Message);
        }

        try
        {
            await _model!.QueueBindAsync(queueName, retryDeadLetterExchangeName, retryQueueName, _retryQueueArguments, cancellationToken: cancellationToken); // only redeliver to the original queue (use _queueName as routing key)
        }
        catch (Exception ex)
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
        catch (Exception ex)
        {
            _logger.LogWarning("Error declaring queue {Message}", ex.Message);
        }
    }

    private async Task<string> ConfigureErrorExchangeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _model!.ExchangeDeclareAsync(_queueConfiguration.ErrorQueueName, ExchangeType.Direct, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error declaring error exchange {Message}", ex.Message);
        }

        return _queueConfiguration.ErrorQueueName;
    }

    private async Task<string> ConfigureErrorQueueAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _model!.QueueDeclareAsync(_queueConfiguration.ErrorQueueName, true, false, false, _utilityQueueArguments, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error declaring error queue {Message}", ex.Message);
        }

        return _queueConfiguration.ErrorQueueName;
    }

    private async Task<string> ConfigureAuditExchangeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _model!.ExchangeDeclareAsync(_queueConfiguration.AuditQueueName, ExchangeType.Direct, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error declaring audit exchange {Message}", ex.Message);
        }

        return _queueConfiguration.AuditQueueName;
    }

    private async Task<string> ConfigureAuditQueueAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _model!.QueueDeclareAsync(_queueConfiguration.AuditQueueName, true, false, false, _utilityQueueArguments, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error declaring audit queue {Message}", ex.Message);
        }
        return _queueConfiguration.AuditQueueName;
    }
}
