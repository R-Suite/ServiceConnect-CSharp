using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Diagnostics;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Failure-path policy for a RabbitMQ client. Given a failed delivery, either re-publishes
/// the message to the per-queue ".Retries" queue (incrementing the retry counter) or,
/// once max retries are exhausted, publishes to the configured error exchange with
/// redacted exception info in the header.
/// </summary>
internal sealed class MessageRetryHandler(
    int maxRetries,
    string errorExchange,
    string consumerQueueName,
    ILogger logger,
    TimeProvider? timeProvider = null)
{
    private readonly int _maxRetries = maxRetries;
    private readonly string _errorExchange = errorExchange ?? throw new ArgumentNullException(nameof(errorExchange));
    private readonly string _consumerQueueName = consumerQueueName ?? throw new ArgumentNullException(nameof(consumerQueueName));
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

            if (candidate < 0 || candidate > _maxRetries)
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

            // Emitted at the increment site so a counter delta corresponds 1:1 with a retry-queue
            // republish, regardless of whether the subsequent BasicPublishAsync ultimately succeeds.
            // messaging.destination.name carries the consumer queue (operator filter key);
            // messaging.serviceconnect.retry.target carries the per-message retry-queue destination.
            ServiceConnectMeter.AddRetryAttempt(new TagList
            {
                { "messaging.system", "rabbitmq" },
                { "messaging.destination.name", _consumerQueueName },
                { "messaging.serviceconnect.retry.target", retryQueueName },
            });

            // Explicit copy avoids the copy-constructor's "any malformed source field throws" risk.
            // The set of fields here mirrors the AMQP BASIC properties RabbitMQ.Client exposes;
            // adding a field to BasicProperties without updating this copy is a silent regression —
            // MessageRetryHandlerCopyPropsTests guards against that.
            var props = new BasicProperties
            {
                ContentType = args.BasicProperties.ContentType,
                ContentEncoding = args.BasicProperties.ContentEncoding,
                DeliveryMode = args.BasicProperties.DeliveryMode,
                Priority = args.BasicProperties.Priority,
                CorrelationId = args.BasicProperties.CorrelationId,
                ReplyTo = args.BasicProperties.ReplyTo,
                Expiration = args.BasicProperties.Expiration,
                MessageId = args.BasicProperties.MessageId,
                Timestamp = args.BasicProperties.Timestamp,
                Type = args.BasicProperties.Type,
                UserId = args.BasicProperties.UserId,
                AppId = args.BasicProperties.AppId,
                ClusterId = args.BasicProperties.ClusterId,
                Headers = HeaderHelpers.ToNullableHeaders(headers),
            };
            // mandatory:true so publisher confirms surface unroutable returns as PublishException;
            // otherwise the broker silently drops the message and we lose the failure signal.
            // The catch in InboundMessageProcessor logs Error and acks-to-break-the-loop on PublishException.
            await channel.BasicPublishAsync(string.Empty, retryQueueName, true, props, args.Body, cancellationToken).ConfigureAwait(false);
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
            HeaderHelpers.SetHeader(headers, HeaderKeys.Exception, JsonSerializer.Serialize(new
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

        // Same field-by-field copy as the retry-publish path — see comment there.
        var errorProps = new BasicProperties
        {
            ContentType = args.BasicProperties.ContentType,
            ContentEncoding = args.BasicProperties.ContentEncoding,
            DeliveryMode = args.BasicProperties.DeliveryMode,
            Priority = args.BasicProperties.Priority,
            CorrelationId = args.BasicProperties.CorrelationId,
            ReplyTo = args.BasicProperties.ReplyTo,
            Expiration = args.BasicProperties.Expiration,
            MessageId = args.BasicProperties.MessageId,
            Timestamp = args.BasicProperties.Timestamp,
            Type = args.BasicProperties.Type,
            UserId = args.BasicProperties.UserId,
            AppId = args.BasicProperties.AppId,
            ClusterId = args.BasicProperties.ClusterId,
            Headers = HeaderHelpers.ToNullableHeaders(headers),
        };
        // mandatory:true — see comment in HandleFailureAsync. PublishException on unroutable
        // surfaces through the InboundMessageProcessor catch; logged at Error and acked to
        // prevent unbounded redelivery.
        await channel.BasicPublishAsync(_errorExchange, string.Empty, true, errorProps, args.Body, cancellationToken).ConfigureAwait(false);
    }
}
