namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Defines RabbitMQ-specific keys used in <c>ITransportConfiguration.ClientSettings</c>.
/// </summary>
public static class RabbitMQSettingKeys
{
    /// <summary>RabbitMQ TCP port.</summary>
    public const string Port = "Port";

    /// <summary>Whether declared queues should be durable.</summary>
    public const string Durable = "Durable";

    /// <summary>Whether declared queues should be exclusive.</summary>
    public const string Exclusive = "Exclusive";

    /// <summary>Whether declared queues should be auto-deleted.</summary>
    public const string AutoDelete = "AutoDelete";

    /// <summary>Additional arguments for the primary queue declaration.</summary>
    public const string Arguments = "Arguments";

    /// <summary>Additional arguments for retry queue declarations.</summary>
    public const string RetryQueueArguments = "RetryQueueArguments";

    /// <summary>Additional arguments for utility queue declarations such as audit and error queues.</summary>
    public const string UtilityQueueArguments = "UtilityQueueArguments";

    /// <summary>Requested prefetch count for consumers.</summary>
    public const string PrefetchCount = "PrefetchCount";

    /// <summary>Whether consumer prefetch configuration should be disabled.</summary>
    public const string DisablePrefetch = "DisablePrefetch";
    /// <summary>Maximum message body size, in bytes.</summary>
    public const string MessageSize = "MessageSize";

    /// <summary>Whether publisher acknowledgements are enabled for outbound publishes.</summary>
    public const string PublisherAcknowledgements = "PublisherAcknowledgements";
    /// <summary>Publish-retry attempt count.</summary>
    public const string RetryCount = "RetryCount";
    /// <summary>
    /// Delay between publish retries, in SECONDS.
    /// Distinct from <see cref="Interfaces.Configuration.ITransportConfiguration.RetryDelay"/>,
    /// which controls the dead-letter message-level retry delay in MILLISECONDS.
    /// </summary>
    public const string RetrySeconds = "RetrySeconds";

    /// <summary>Whether AMQP heartbeats are enabled for the connection.</summary>
    public const string HeartbeatEnabled = "HeartbeatEnabled";
    /// <summary>Heartbeat interval, in seconds.</summary>
    public const string HeartbeatTime = "HeartbeatTime";

    /// <summary>
    /// Maximum time to wait for a broker acknowledgement when publishing under publisher confirms.
    /// Accepts a <see cref="System.TimeSpan"/>; defaults to 30 seconds.
    /// </summary>
    public const string PublishTimeout = "PublishTimeout";

    /// <summary>
    /// Maximum outstanding publisher-confirms per producer channel before publishes back-pressure.
    /// Without this cap, a stalled broker can let the RabbitMQ.Client tracker grow unboundedly.
    /// Tunable via <c>SetClientSetting("MaxOutstandingPublishConfirms", N)</c>; defaults to 256.
    /// </summary>
    public const string MaxOutstandingPublishConfirms = nameof(MaxOutstandingPublishConfirms);
}
