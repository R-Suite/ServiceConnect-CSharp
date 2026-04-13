namespace ServiceConnect.Client.RabbitMQ;

public static class RabbitMQSettingKeys
{
    public const string Port = "Port";
    public const string Durable = "Durable";
    public const string Exclusive = "Exclusive";
    public const string AutoDelete = "AutoDelete";
    public const string Arguments = "Arguments";
    public const string RetryQueueArguments = "RetryQueueArguments";
    public const string UtilityQueueArguments = "UtilityQueueArguments";
    public const string PrefetchCount = "PrefetchCount";
    public const string DisablePrefetch = "DisablePrefetch";
    /// <summary>Maximum message body size, in bytes.</summary>
    public const string MessageSize = "MessageSize";
    public const string PublisherAcknowledgements = "PublisherAcknowledgements";
    /// <summary>Publish-retry attempt count.</summary>
    public const string RetryCount = "RetryCount";
    /// <summary>
    /// Delay between publish retries, in SECONDS.
    /// Distinct from <see cref="Interfaces.Configuration.ITransportConfiguration.RetryDelay"/>,
    /// which controls the dead-letter message-level retry delay in MILLISECONDS.
    /// </summary>
    public const string RetrySeconds = "RetrySeconds";
    public const string HeartbeatEnabled = "HeartbeatEnabled";
    /// <summary>Heartbeat interval, in seconds.</summary>
    public const string HeartbeatTime = "HeartbeatTime";
}
