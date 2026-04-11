using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using System.Collections.Concurrent;

namespace ServiceConnect.Client.RabbitMQ;

public class Consumer : IConsumer
{
    private IModel? _model;
    private bool _durable;
    private int _retryDelay;
    private bool _exclusive;
    private bool _autoDelete;
    private IServiceConnectConnection? _connection;
    private readonly ILogger<Consumer> _logger;
    private readonly ITransportConfiguration _transportConfiguration;
    private readonly IQueueConfiguration _queueConfiguration;
    private IDictionary<string, object> _queueArguments = new Dictionary<string, object>();
    private IDictionary<string, object> _retryQueueArguments = new Dictionary<string, object>();
    private IDictionary<string, object> _utilityQueueArguments = new Dictionary<string, object>();
    private readonly ConcurrentBag<Client> _clients = new();
    private readonly IBusConfiguration _busConfiguration;

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

    public Task StartConsumingAsync(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler)
    {
        var clientSettings = _transportConfiguration.ClientSettings;

        _durable = !clientSettings.ContainsKey("Durable") || (bool)clientSettings["Durable"];
        _exclusive = clientSettings.ContainsKey("Exclusive") && (bool)clientSettings["Exclusive"];
        _autoDelete = clientSettings.ContainsKey("AutoDelete") && (bool)clientSettings["AutoDelete"];
        _queueArguments = clientSettings.ContainsKey("Arguments") ? (IDictionary<string, object>)clientSettings["Arguments"] : new Dictionary<string, object>();
        _retryQueueArguments = clientSettings.ContainsKey("RetryQueueArguments") ? (IDictionary<string, object>)clientSettings["RetryQueueArguments"] : new Dictionary<string, object>();
        _utilityQueueArguments = clientSettings.ContainsKey("UtilityQueueArguments") ? (IDictionary<string, object>)clientSettings["UtilityQueueArguments"] : new Dictionary<string, object>();
        _retryDelay = _transportConfiguration.RetryDelay;

        _connection ??= new Connection(_transportConfiguration, queueName, _logger);

        _model ??= _connection.CreateModel();

        // Configure exchanges
        foreach (string messageType in messageTypes)
        {
            ConfigureExchange(messageType, "fanout");
        }

        // Configure queue
        ConfigureQueue(queueName);

        // Purge all messages on queue
        if (_queueConfiguration.PurgeQueueOnStartup)
        {
            _logger.LogDebug("Purging queue");
            _ = _model.QueuePurge(queueName);
        }

        // Configure retry queue (but only if retries are expected)
        if (_transportConfiguration.MaxRetries > 0)
        {
            ConfigureRetryQueue(queueName);
        }

        // Configure Error Queue/Exchange
        string errorExchange = ConfigureErrorExchange();
        string errorQueue = ConfigureErrorQueue();

        if (!string.IsNullOrEmpty(errorExchange))
        {
            _model.QueueBind(errorQueue, errorExchange, string.Empty, _utilityQueueArguments);
        }

        // Configure Audit Queue/Exchange
        if (_queueConfiguration.AuditingEnabled)
        {
            string auditExchange = ConfigureAuditExchange();
            string auditQueue = ConfigureAuditQueue();

            if (!string.IsNullOrEmpty(auditExchange))
            {
                _model.QueueBind(auditQueue, auditExchange, string.Empty, _utilityQueueArguments);
            }
        }

        int clientCount = _busConfiguration.ConsumerCount;

        for (int i = 0; i < clientCount; i++)
        {
            Client client = new(_connection, _transportConfiguration, _queueConfiguration, _logger);
            client.StartConsuming(eventHandler, queueName);
            foreach (string messageType in messageTypes)
            {
                client.ConsumeMessageType(messageType);
            }
            _clients.Add(client);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (Client consumer in _clients)
        {
            consumer.Dispose();
        }

        _model?.Dispose();
        _connection?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void ConfigureExchange(string exchangeName, string type)
    {
        try
        {
            // Hard code auto delete and durable to sensible defaults so that producers and consumers dont try to declare exchanges with different settings.
            _model!.ExchangeDeclare(exchangeName, type, true, false, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error declaring exchange {Message}", ex.Message);
        }
    }

    private void ConfigureQueue(string queueName)
    {
        try
        {
            _ = _model!.QueueDeclare(queueName, _durable, _exclusive, _autoDelete, _queueArguments);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error declaring queue - {Message}", ex.Message);
        }
    }

    private void ConfigureRetryQueue(string queueName)
    {
        // When message goes to retry queue, it falls-through to dead-letter exchange (after _retryDelay)
        // dead-letter exchange is of type "direct" and bound to the original queue.
        string retryQueueName = queueName + ".Retries";
        string retryDeadLetterExchangeName = queueName + ".Retries.DeadLetter";

        try
        {
            _model!.ExchangeDeclare(retryDeadLetterExchangeName, "direct", _durable, _autoDelete, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error declaring dead letter exchange - {Message}", ex.Message);
        }

        try
        {
            _model!.QueueBind(queueName, retryDeadLetterExchangeName, retryQueueName, _retryQueueArguments); // only redeliver to the original queue (use _queueName as routing key)
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error binding dead letter queue - {Message}", ex.Message);
        }

        Dictionary<string, object> arguments = new(_retryQueueArguments)
        {
            {"x-dead-letter-exchange", retryDeadLetterExchangeName},
            {"x-message-ttl", _retryDelay}
        };

        try
        {
            // We never have consumers on the retry queue.  Therefore set autodelete to false.
            _ = _model!.QueueDeclare(retryQueueName, _durable, false, false, arguments);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error declaring queue {Message}", ex.Message);
        }
    }

    private string ConfigureErrorExchange()
    {
        try
        {
            _model!.ExchangeDeclare(_queueConfiguration.ErrorQueueName, "direct");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error declaring error exchange {Message}", ex.Message);
        }

        return _queueConfiguration.ErrorQueueName;
    }

    private string ConfigureErrorQueue()
    {
        try
        {
            _ = _model!.QueueDeclare(_queueConfiguration.ErrorQueueName, true, false, false, _utilityQueueArguments);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error declaring error queue {Message}", ex.Message);
        }

        return _queueConfiguration.ErrorQueueName;
    }

    private string ConfigureAuditExchange()
    {
        try
        {
            _model!.ExchangeDeclare(_queueConfiguration.AuditQueueName, "direct");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error declaring audit exchange {Message}", ex.Message);
        }

        return _queueConfiguration.AuditQueueName;
    }

    private string ConfigureAuditQueue()
    {
        try
        {
            _ = _model!.QueueDeclare(_queueConfiguration.AuditQueueName, true, false, false, _utilityQueueArguments);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error declaring audit queue {Message}", ex.Message);
        }
        return _queueConfiguration.AuditQueueName;
    }
}
