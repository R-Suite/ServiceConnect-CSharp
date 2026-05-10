using RabbitMQ.Client;

namespace ServiceConnect.Client.RabbitMQ;

internal static class BasicPropertiesCopier
{
    /// <summary>
    /// Returns a new <see cref="BasicProperties"/> populated by reading each AMQP
    /// BASIC field from <paramref name="source"/> individually. The <see cref="BasicProperties"/>
    /// copy-constructor performs internal validation that throws on a single malformed
    /// source field — the consumer paths must not be torn down by a quirky inbound
    /// delivery, so retry / error / audit publishes all use this helper instead of
    /// <c>new BasicProperties(args.BasicProperties)</c>.
    /// </summary>
    /// <remarks>
    /// Adding a field to RabbitMQ.Client's <see cref="IReadOnlyBasicProperties"/> surface
    /// without updating this helper is a silent regression — the new field would be
    /// dropped from every retry/error/audit publish. <c>BasicPropertiesCopierTests</c>
    /// guards against that.
    /// </remarks>
    public static BasicProperties CreateCopy(IReadOnlyBasicProperties source, IDictionary<string, object?>? headers)
    {
        return new BasicProperties
        {
            ContentType = source.ContentType,
            ContentEncoding = source.ContentEncoding,
            DeliveryMode = source.DeliveryMode,
            Priority = source.Priority,
            CorrelationId = source.CorrelationId,
            ReplyTo = source.ReplyTo,
            Expiration = source.Expiration,
            MessageId = source.MessageId,
            Timestamp = source.Timestamp,
            Type = source.Type,
            UserId = source.UserId,
            AppId = source.AppId,
            ClusterId = source.ClusterId,
            Headers = headers,
        };
    }
}
