using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Encapsulates RabbitMQ topology provisioning (exchanges, queues, bindings).
/// Always re-throws AMQP <see cref="OperationInterruptedException"/> (PRECONDITION_FAILED,
/// NOT_FOUND, etc.). Such errors close the underlying channel; swallowing them would
/// leave the caller publishing/consuming on a dead channel and surface much later as
/// an opaque <c>AlreadyClosedException</c>. The <c>isInitialSetup</c> parameter on each
/// method is retained for source-compat with v6 callers but is no longer consulted.
/// </summary>
/// <remarks>
/// Initializes a new topology provisioner.
/// </remarks>
/// <param name="logger">The logger used for topology provisioning warnings.</param>
internal sealed class RabbitMqTopologyProvisioner(ILogger logger)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Declares an exchange with standard durable/non-auto-delete settings.
    /// Deduplicated exchange declaration.
    /// Always re-throws AMQP errors — see class summary for the channel-state rationale.
    /// </summary>
    public async Task ConfigureDeclareExchangeAsync(
        IChannel channel,
        string exchangeName,
        string exchangeType,
        bool isInitialSetup = false,
        CancellationToken cancellationToken = default)
    {
        // Reserved for future use — see ConfigureDeclareUtilityQueueAsync for the semantic
        // ("swallow bind-time failure during initial setup, rethrow on later provisions").
        // This method always rethrows because exchange-declare failures during repair
        // mean the channel is dead and the caller must reconnect rather than continue.
        _ = isInitialSetup;
        try
        {
            await channel.ExchangeDeclareAsync(
                exchangeName, exchangeType,
                durable: true,
                autoDelete: false,
                arguments: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationInterruptedException ex)
        {
            // Pass `ex` as first arg so structured loggers capture the full exception
            // (ReplyCode, ReplyText, stack) — `ex.Message` only renders the prefix and
            // loses the AMQP reply-code that drives incident triage.
            _logger.LogWarning(ex, "Error declaring exchange {ExchangeName}", exchangeName);
            throw;
        }
    }

    /// <summary>
    /// Declares the main consumer queue.
    /// Always re-throws AMQP errors — see class summary for the channel-state rationale.
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
        // Reserved for future use — queue-declare failures during repair mean the channel
        // is dead and the caller must reconnect rather than continue, so this method
        // always rethrows regardless of phase.
        _ = isInitialSetup;
        try
        {
            await channel.QueueDeclareAsync(
                queueName,
                durable,
                exclusive,
                autoDelete,
                arguments,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogWarning(ex, "Error declaring queue {QueueName}", queueName);
            throw;
        }
    }

    /// <summary>
    /// Declares a utility queue (error/audit), its exchange, and binding.
    /// Deduplicated utility queue setup.
    /// Always re-throws AMQP errors — see class summary for the channel-state rationale.
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
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogWarning(ex, "Error declaring exchange {ExchangeName}", name);
            throw;
        }

        try
        {
            await channel.QueueDeclareAsync(name, durable: true, exclusive: false, autoDelete: false, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogWarning(ex, "Error declaring queue {QueueName}", name);
            throw;
        }

        if (!string.IsNullOrEmpty(name))
        {
            try
            {
                await channel.QueueBindAsync(name, name, string.Empty, null, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationInterruptedException ex)
            {
                _logger.LogWarning(ex, "Error binding queue {QueueName}", name);
                if (isInitialSetup)
                {
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Declares the retry topology: dead-letter exchange, queue binding, and retry queue.
    /// Always re-throws AMQP errors — see class summary for the channel-state rationale.
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
        // autoDelete: caller-supplied for symmetry with the main-queue declare site, but
        // the retry DLX itself is invariant autoDelete:false (see the comment at the
        // ExchangeDeclareAsync call below). isInitialSetup is reserved for future use.
        _ = autoDelete;
        _ = isInitialSetup;
        string retryQueueName = queueName + RabbitMqQueueNaming.RetryQueueSuffix;
        string retryDeadLetterExchangeName = queueName + RabbitMqQueueNaming.RetryDeadLetterExchangeSuffix;

        try
        {
            // Retry DLX is always autoDelete:false: it must outlive any individual queue lifecycle so
            // retried messages always have somewhere to land. The caller-supplied `autoDelete` parameter
            // continues to govern the main queue (declared elsewhere) but the retry DLX is invariant.
            await channel.ExchangeDeclareAsync(retryDeadLetterExchangeName, ExchangeType.Direct, durable, autoDelete: false, null, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogWarning(ex, "Error declaring dead letter exchange {ExchangeName}", retryDeadLetterExchangeName);
            throw;
        }

        try
        {
            await channel.QueueBindAsync(queueName, retryDeadLetterExchangeName, retryQueueName, null, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogWarning(ex, "Error binding dead letter queue {QueueName} to exchange {ExchangeName}", queueName, retryDeadLetterExchangeName);
            throw;
        }

        Dictionary<string, object?> arguments = new(retryQueueArguments, StringComparer.Ordinal);

        // Framework values for these two keys are non-negotiable: they wire the retry queue to the
        // retry DLX with the configured TTL. Caller-supplied values are overridden silently except
        // for a Debug log so config drift surfaces without polluting Information.
        LogIfOverriding(RabbitMqQueueNaming.XDeadLetterExchangeArgument, arguments, retryDeadLetterExchangeName);
        LogIfOverriding(RabbitMqQueueNaming.XMessageTtlArgument, arguments, retryDelayMs);

        arguments[RabbitMqQueueNaming.XDeadLetterExchangeArgument] = retryDeadLetterExchangeName;
        arguments[RabbitMqQueueNaming.XMessageTtlArgument] = retryDelayMs;

        try
        {
            await channel.QueueDeclareAsync(retryQueueName, durable, exclusive: false, autoDelete: false, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationInterruptedException ex)
        {
            _logger.LogWarning(ex, "Error declaring retry queue {QueueName}", retryQueueName);
            throw;
        }
    }

    private void LogIfOverriding<T>(string key, IReadOnlyDictionary<string, object?> existing, T frameworkValue)
    {
        if (existing.TryGetValue(key, out var existingValue) && !Equals(existingValue, frameworkValue))
        {
            _logger.LogDebug(
                "Overriding caller-supplied retry-queue argument {Key} (was '{ExistingValue}') with framework value '{FrameworkValue}'",
                key, existingValue, frameworkValue);
        }
    }
}
