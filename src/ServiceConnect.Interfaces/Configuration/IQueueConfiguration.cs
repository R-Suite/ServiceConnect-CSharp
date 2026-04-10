namespace ServiceConnect.Interfaces.Configuration;

public interface IQueueConfiguration
{
    string QueueName { get; set; }
    string ErrorQueueName { get; set; }
    string AuditQueueName { get; set; }
    string HeartbeatQueueName { get; set; }
    bool AuditingEnabled { get; set; }
    bool DisableErrors { get; set; }
    bool PurgeQueueOnStartup { get; set; }
    IDictionary<string, IList<string>> QueueMappings { get; set; }
    void AddQueueMapping(Type messageType, string queue);
    void AddQueueMapping(Type messageType, IList<string> queues);
}
