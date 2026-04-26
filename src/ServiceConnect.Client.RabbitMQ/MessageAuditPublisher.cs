using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
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

        var props = new BasicProperties(args.BasicProperties) { Headers = HeaderHelpers.ToNullableHeaders(headers) };
        await channel.BasicPublishAsync(
            _queueConfiguration.AuditQueueName,
            string.Empty, // audit direct-exchange binds with empty routing key; AuditRoutingKey is ignored
            mandatory: false,
            props,
            args.Body,
            cancellationToken).ConfigureAwait(false);
    }
}
