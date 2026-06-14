namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Suffix conventions and AMQP argument keys shared across the consumer and producer sides.
/// </summary>
internal static class RabbitMqQueueNaming
{
    public const string RetryQueueSuffix = ".Retries";
    public const string RetryDeadLetterExchangeSuffix = ".Retries.DeadLetter";

    // AMQP-defined queue argument keys
    public const string XDeadLetterExchangeArgument = "x-dead-letter-exchange";
    public const string XMessageTtlArgument = "x-message-ttl";
}
