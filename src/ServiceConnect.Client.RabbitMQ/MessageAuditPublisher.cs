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

    public MessageAuditPublisher(IQueueConfiguration queueConfiguration)
    {
        _queueConfiguration = queueConfiguration ?? throw new ArgumentNullException(nameof(queueConfiguration));
    }

    public async Task PublishAuditIfEnabledAsync(
        IChannel channel,
        BasicDeliverEventArgs args,
        Dictionary<string, object> headers,
        CancellationToken cancellationToken = default)
    {
        if (!_queueConfiguration.AuditingEnabled)
            return;

        string? messageType = null;
        if (headers.TryGetValue(HeaderKeys.MessageType, out var raw))
            messageType = HeaderDecoder.Decode(raw);

        if (messageType == HeaderKeys.ByteStream)
            return;

        var props = new BasicProperties(args.BasicProperties) { Headers = HeaderHelpers.ToNullableHeaders(headers) };
        await channel.BasicPublishAsync(
            _queueConfiguration.AuditQueueName,
            _queueConfiguration.AuditRoutingKey ?? string.Empty,
            mandatory: false,
            props,
            args.Body,
            cancellationToken).ConfigureAwait(false);
    }
}
