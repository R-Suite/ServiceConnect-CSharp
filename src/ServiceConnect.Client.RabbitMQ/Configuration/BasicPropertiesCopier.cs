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
    /// <para>
    /// Adding a field to RabbitMQ.Client's <see cref="IReadOnlyBasicProperties"/> surface
    /// without updating this helper is a silent regression — the new field would be
    /// dropped from every retry/error/audit publish. <c>BasicPropertiesCopierTests</c>
    /// guards against that.
    /// </para>
    /// <para>
    /// <b>Identity / provenance fields are deliberately NOT copied:</b>
    /// <c>UserId</c>, <c>AppId</c>, and <c>ClusterId</c> describe the original publishing
    /// connection. On a broker with <c>validated_user_id</c> enabled (a documented RabbitMQ
    /// feature), republishing with a non-matching <c>UserId</c> is rejected with
    /// <c>406 PRECONDITION_FAILED</c>, which closes the publish channel and flips the
    /// consumer's broker-cancel flag — a single malicious inbound message could otherwise
    /// disable consumption on the pod. Even without that broker policy, asserting an
    /// identity the consumer connection does not hold misleads downstream audit trails.
    /// Leaving these fields null on retry/audit/error republishes is the safe behaviour.
    /// </para>
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
            // UserId / AppId / ClusterId intentionally omitted — see <remarks>.
            Headers = headers,
        };
    }
}
