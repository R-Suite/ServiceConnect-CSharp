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
    public const string MessageSize = "MessageSize";
    public const string PublisherAcknowledgements = "PublisherAcknowledgements";
    public const string RetryCount = "RetryCount";
    public const string RetrySeconds = "RetrySeconds";
    public const string HeartbeatEnabled = "HeartbeatEnabled";
    public const string HeartbeatTime = "HeartbeatTime";
}
