using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace ServiceConnect.Client.RabbitMQ;

    /// <summary>
    /// Encapsulates RabbitMQ topology provisioning (exchanges, queues, bindings).
    /// Catches AMQP PRECONDITION_FAILED errors and re-throws them on initial setup.
    /// </summary>
public sealed class RabbitMqTopologyProvisioner
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new topology provisioner.
    /// </summary>
    /// <param name="logger">The logger used for topology provisioning warnings.</param>
    public RabbitMqTopologyProvisioner(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Declares an exchange with standard durable/non-auto-delete settings.
    /// Deduplicated exchange declaration.
    /// Swallows OperationInterruptedException unless isInitialSetup is true.
    /// </summary>
    public async Task ConfigureDeclareExchangeAsync(
        IChannel channel,
        string exchangeName,
        string exchangeType,
        bool isInitialSetup = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await channel.ExchangeDeclareAsync(
                exchangeName, exchangeType,
                durable: true,
                autoDelete: false,
                arguments: null,
                cancellationToken: cancellationToken);
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogWarning("Error declaring exchange {ExchangeName}: {Message}", exchangeName, ex.Message);
            if (isInitialSetup) throw;
        }
    }

    /// <summary>
    /// Declares the main consumer queue.
    /// Swallows OperationInterruptedException unless isInitialSetup is true.
    /// </summary>
    public async Task ConfigureDeclareQueueAsync(
        IChannel channel,
        string queueName,
        bool durable,
        bool exclusive,
        bool autoDelete,
        IDictionary<string, object?> arguments,
        bool isInitialSetup = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await channel.QueueDeclareAsync(
                queueName,
                durable,
                exclusive,
                autoDelete,
                arguments,
                cancellationToken: cancellationToken);
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogWarning("Error declaring queue {QueueName}: {Message}", queueName, ex.Message);
            if (isInitialSetup) throw;
        }
    }

    /// <summary>
    /// Declares a utility queue (error/audit), its exchange, and binding.
    /// Deduplicated utility queue setup.
    /// Swallows OperationInterruptedException unless isInitialSetup is true.
    /// </summary>
    public async Task ConfigureDeclareUtilityQueueAsync(
        IChannel channel,
        string name,
        IDictionary<string, object?> arguments,
        bool isInitialSetup = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await channel.ExchangeDeclareAsync(
                name,
                ExchangeType.Direct,
                durable: true,
                autoDelete: false,
                arguments: null,
                cancellationToken: cancellationToken);
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogWarning("Error declaring exchange {ExchangeName}: {Message}", name, ex.Message);
            if (isInitialSetup) throw;
        }

        try
        {
            await channel.QueueDeclareAsync(name, durable: true, exclusive: false, autoDelete: false, arguments, cancellationToken: cancellationToken);
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogWarning("Error declaring queue {QueueName}: {Message}", name, ex.Message);
            if (isInitialSetup) throw;
        }

        if (!string.IsNullOrEmpty(name))
        {
            try
            {
                await channel.QueueBindAsync(name, name, string.Empty, null, cancellationToken: cancellationToken);
            }
            catch (OperationInterruptedException ex)
            {
                _logger.LogWarning("Error binding queue {QueueName}: {Message}", name, ex.Message);
                if (isInitialSetup) throw;
            }
        }
    }

    /// <summary>
    /// Declares the retry topology: dead-letter exchange, queue binding, and retry queue.
    /// Swallows OperationInterruptedException unless isInitialSetup is true.
    /// </summary>
    public async Task ConfigureRetryTopologyAsync(
        IChannel channel,
        string queueName,
        bool durable,
        bool autoDelete,
        int retryDelayMs,
        IDictionary<string, object?> retryQueueArguments,
        bool isInitialSetup = false,
        CancellationToken cancellationToken = default)
    {
        string retryQueueName = queueName + RabbitMqQueueNaming.RetryQueueSuffix;
        string retryDeadLetterExchangeName = queueName + RabbitMqQueueNaming.RetryDeadLetterExchangeSuffix;

        try
        {
            await channel.ExchangeDeclareAsync(retryDeadLetterExchangeName, ExchangeType.Direct, durable, autoDelete, null, cancellationToken: cancellationToken);
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogWarning("Error declaring dead letter exchange - {Message}", ex.Message);
            if (isInitialSetup) throw;
        }

        try
        {
            await channel.QueueBindAsync(queueName, retryDeadLetterExchangeName, retryQueueName, null, cancellationToken: cancellationToken);
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogWarning("Error binding dead letter queue - {Message}", ex.Message);
            if (isInitialSetup) throw;
        }

        Dictionary<string, object?> arguments = new(retryQueueArguments)
        {
            {RabbitMqQueueNaming.XDeadLetterExchangeArgument, retryDeadLetterExchangeName},
            {RabbitMqQueueNaming.XMessageTtlArgument, retryDelayMs}
        };

        try
        {
            await channel.QueueDeclareAsync(retryQueueName, durable, exclusive: false, autoDelete: false, arguments, cancellationToken: cancellationToken);
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogWarning("Error declaring queue {Message}", ex.Message);
            if (isInitialSetup) throw;
        }
    }
}
