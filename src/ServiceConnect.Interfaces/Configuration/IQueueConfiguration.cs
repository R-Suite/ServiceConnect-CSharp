namespace ServiceConnect.Interfaces.Configuration;

public interface IQueueConfiguration
{
    string QueueName { get; set; }
    string ErrorQueueName { get; set; }
    string AuditQueueName { get; set; }
    bool AuditingEnabled { get; set; }
    bool DisableErrors { get; set; }
    bool PurgeQueueOnStartup { get; set; }
    IReadOnlyDictionary<string, IReadOnlyList<string>> QueueMappings { get; }
    void AddQueueMapping(Type messageType, string queue);
    void AddQueueMapping(Type messageType, IList<string> queues);
    bool TryGetQueueMapping(Type messageType, out IReadOnlyList<string> queues);
}
