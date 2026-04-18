using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Failure-path policy for a RabbitMQ client. Given a failed delivery, either re-publishes
/// the message to the per-queue ".Retries" queue (incrementing the retry counter) or,
/// once max retries are exhausted, publishes to the configured error exchange with
/// redacted exception info in the header.
/// </summary>
internal sealed class MessageRetryHandler
{
    private readonly int _maxRetries;
    private readonly string _errorExchange;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public MessageRetryHandler(int maxRetries, string errorExchange, ILogger logger, TimeProvider? timeProvider = null)
    {
        _maxRetries = maxRetries;
        _errorExchange = errorExchange ?? throw new ArgumentNullException(nameof(errorExchange));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task HandleFailureAsync(
        IChannel channel,
        string retryQueueName,
        BasicDeliverEventArgs args,
        Dictionary<string, object> headers,
        Exception? ex,
        CancellationToken cancellationToken = default)
    {
        int retryCount = 0;
        if (headers.TryGetValue(HeaderKeys.RetryCount, out var raw))
        {
            int candidate = raw is int i ? i
                : (raw is not null && int.TryParse(raw.ToString(), out var parsed) ? parsed : -1);
            if (candidate >= 0 && candidate <= _maxRetries + 1)
                retryCount = candidate;
        }

        if (retryCount < _maxRetries)
        {
            retryCount++;
            HeaderHelpers.SetHeader(headers, HeaderKeys.RetryCount, retryCount);
            var props = new BasicProperties(args.BasicProperties)
            {
                Headers = HeaderHelpers.ToNullableHeaders(headers)
            };
            await channel.BasicPublishAsync(string.Empty, retryQueueName, false, props, args.Body, cancellationToken).ConfigureAwait(false);
            return;
        }

        await PublishErrorAsync(channel, args, headers, ex, logAsMaxRetries: true, cancellationToken).ConfigureAwait(false);
    }

    public Task HandleTerminalFailureAsync(
        IChannel channel,
        BasicDeliverEventArgs args,
        Dictionary<string, object> headers,
        Exception ex,
        CancellationToken cancellationToken = default)
    {
        return PublishErrorAsync(channel, args, headers, ex, logAsMaxRetries: false, cancellationToken);
    }

    private async Task PublishErrorAsync(
        IChannel channel,
        BasicDeliverEventArgs args,
        Dictionary<string, object> headers,
        Exception? ex,
        bool logAsMaxRetries,
        CancellationToken cancellationToken)
    {
        if (ex != null)
        {
            HeaderHelpers.SetHeader(headers, HeaderKeys.Exception, JsonConvert.SerializeObject(new
            {
                TimeStamp = _timeProvider.GetUtcNow().UtcDateTime,
                ExceptionType = ex.GetType().FullName,
                Message = HeaderHelpers.GetErrorMessage(ex)
            }));
        }

        if (logAsMaxRetries)
        {
            if (ex != null)
                _logger.LogError(ex, "Max retries exceeded for MessageId {MessageId}", args.BasicProperties.MessageId);
            else
                _logger.LogError("Max retries exceeded for MessageId {MessageId}", args.BasicProperties.MessageId);
        }
        else
        {
            _logger.LogError(ex, "Rejecting permanently invalid inbound message with MessageId {MessageId}", args.BasicProperties.MessageId);
        }

        var errorProps = new BasicProperties(args.BasicProperties)
        {
            Headers = HeaderHelpers.ToNullableHeaders(headers)
        };
        await channel.BasicPublishAsync(_errorExchange, string.Empty, false, errorProps, args.Body, cancellationToken).ConfigureAwait(false);
    }
}
