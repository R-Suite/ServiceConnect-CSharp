using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Success-path policy for a RabbitMQ client. Publishes a copy of a successfully
/// processed message to the audit exchange when auditing is enabled. Skips byte-stream
/// messages to avoid auditing raw stream frames.
/// </summary>
internal sealed class MessageAuditPublisher
{
    private readonly IQueueConfiguration _queueConfiguration;
    private readonly ILogger<MessageAuditPublisher> _logger;

    public MessageAuditPublisher(
        IQueueConfiguration queueConfiguration,
        ILogger<MessageAuditPublisher>? logger = null)
    {
        _queueConfiguration = queueConfiguration ?? throw new ArgumentNullException(nameof(queueConfiguration));
        _logger = logger ?? NullLogger<MessageAuditPublisher>.Instance;

        // Warn once at construction: AuditRoutingKey is ignored at publish time because the
        // audit direct exchange is bound with an empty routing key. A non-empty value would
        // route to nothing with mandatory=false, silently dropping all audit messages.
        if (!string.IsNullOrEmpty(queueConfiguration.AuditRoutingKey))
        {
            _logger.LogWarning(
                "AuditRoutingKey is configured to \"{RoutingKey}\" but the audit direct exchange " +
                "is bound with an empty routing key. The configured value will be ignored and " +
                "routingKey=\"\" will be used at publish time to avoid silent message drops.",
                queueConfiguration.AuditRoutingKey);
        }
    }

    public async Task PublishAuditIfEnabledAsync(
        IChannel channel,
        BasicDeliverEventArgs args,
        Dictionary<string, object> headers,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_queueConfiguration.AuditingEnabled)
        {
            return;
        }

        string? messageType = null;
        if (headers.TryGetValue(HeaderKeys.MessageType, out var raw))
        {
            messageType = HeaderDecoder.Decode(raw);
        }

        if (string.Equals(messageType, HeaderKeys.ByteStream, StringComparison.Ordinal))
        {
            return;
        }

        // Field-by-field copy via BasicPropertiesCopier rather than the BasicProperties
        // copy-constructor: the ctor's "any malformed source field throws" risk would
        // otherwise propagate out of PublishAuditIfEnabledAsync — caught upstream and
        // acked silently — silently dropping audits whenever an inbound delivery had a
        // quirky property. MessageRetryHandler avoids the ctor for the same reason.
        var props = BasicPropertiesCopier.CreateCopy(args.BasicProperties, HeaderHelpers.ToNullableHeaders(headers));
        // Audit is best-effort: the message has already been processed successfully, so a
        // failure to publish the audit copy must not propagate back into the consumer pipeline
        // (which would nack-with-requeue and re-run the handler against an idempotent surface).
        // A broker quota or partition affecting only the audit queue would otherwise fail every
        // successfully-handled delivery.
        try
        {
            await channel.BasicPublishAsync(
                _queueConfiguration.AuditQueueName,
                string.Empty, // audit direct-exchange binds with empty routing key; AuditRoutingKey is ignored
                mandatory: false,
                props,
                args.Body,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Audit publish failed for message {MessageType}; original delivery is acked normally.",
                messageType ?? "<unknown>");
            // Audit drops are observable through the messaging.serviceconnect.audit.drops
            // counter so operators can alert on broker-side audit failures without parsing
            // logs. The audit queue is a single global destination per the spec — no
            // messaging.destination.name tag.
            ServiceConnectMeter.AddAuditDrop(new TagList
            {
                { "messaging.system", "rabbitmq" },
                { "error.type", ExceptionTypeMapper.Map(ex) },
            });
        }
    }
}
