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
internal sealed class MessageRetryHandler(int maxRetries, string errorExchange, ILogger logger, TimeProvider? timeProvider = null)
{
    private readonly int _maxRetries = maxRetries;
    private readonly string _errorExchange = errorExchange ?? throw new ArgumentNullException(nameof(errorExchange));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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
            // int fast-path preserved for performance (native C# producers stamp int).
            // Non-.NET clients stamp an AMQP string which arrives as UTF-8 byte[]; use
            // HeaderDecoder.Decode so that "3" encoded as byte[] parses correctly.
            int candidate;
            if (raw is int i)
            {
                candidate = i;
            }
            else
            {
                var decoded = HeaderDecoder.Decode(raw);
                candidate = decoded is not null && int.TryParse(decoded, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
            }

            if (candidate < 0 || candidate > _maxRetries + 1)
            {
                // Never silently reset to 0 here — a corrupt or attacker-controlled header
                // would otherwise force infinite retries. Route to error so an operator
                // can see the malformed value instead of the broker looping forever.
                _logger.LogWarning(
                    "Malformed or out-of-range RetryCount header '{RetryCount}' for MessageId {MessageId}; routing to error exchange.",
                    raw, args.BasicProperties.MessageId);
                await PublishErrorAsync(channel, args, headers, ex, logAsMaxRetries: false, cancellationToken).ConfigureAwait(false);
                return;
            }
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
            {
                _logger.LogError(ex, "Max retries exceeded for MessageId {MessageId}", args.BasicProperties.MessageId);
            }
            else
            {
                _logger.LogError("Max retries exceeded for MessageId {MessageId}", args.BasicProperties.MessageId);
            }
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
