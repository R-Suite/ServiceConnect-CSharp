using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

public sealed class QueueConfiguration : IQueueConfiguration
{
    public string QueueName { get; set; } = "";
    public string ErrorQueueName { get; set; } = "errors";
    public string AuditQueueName { get; set; } = "audit";
    public bool AuditingEnabled { get; set; }
    public bool DisableErrors { get; set; }
    public bool PurgeQueueOnStartup { get; set; }
    public IDictionary<string, IList<string>> QueueMappings { get; set; } = new Dictionary<string, IList<string>>();

    public void AddQueueMapping(Type messageType, string queue)
    {
        string key = messageType.FullName!;
        if (!QueueMappings.ContainsKey(key))
            QueueMappings[key] = new List<string>();
        QueueMappings[key].Add(queue);
    }

    public void AddQueueMapping(Type messageType, IList<string> queues)
    {
        string key = messageType.FullName!;
        if (!QueueMappings.ContainsKey(key))
            QueueMappings[key] = new List<string>();
        foreach (string queue in queues)
            QueueMappings[key].Add(queue);
    }
}
