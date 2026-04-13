using Microsoft.Extensions.Logging;
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
    private readonly string _auditExchange;
    private readonly IQueueConfiguration _queueConfiguration;
    private readonly ILogger _logger;

    public MessageAuditPublisher(string auditExchange, IQueueConfiguration queueConfiguration, ILogger logger)
    {
        _auditExchange = auditExchange ?? throw new ArgumentNullException(nameof(auditExchange));
        _queueConfiguration = queueConfiguration ?? throw new ArgumentNullException(nameof(queueConfiguration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task PublishAuditIfEnabledAsync(IChannel channel, BasicDeliverEventArgs args, Dictionary<string, object> headers)
    {
        if (!_queueConfiguration.AuditingEnabled)
            return;

        string? messageType = null;
        if (headers.TryGetValue(HeaderKeys.MessageType, out var raw))
            messageType = HeaderDecoder.Decode(raw);

        if (messageType == HeaderKeys.ByteStream)
            return;

        var props = new BasicProperties(args.BasicProperties) { Headers = HeaderHelpers.ToNullableHeaders(headers) };
        await channel.BasicPublishAsync(_auditExchange, string.Empty, mandatory: false, props, args.Body).ConfigureAwait(false);
    }
}
