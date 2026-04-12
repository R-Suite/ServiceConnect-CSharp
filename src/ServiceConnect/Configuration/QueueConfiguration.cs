using System.Collections.Concurrent;
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
    public IDictionary<string, IList<string>> QueueMappings { get; set; } = new ConcurrentDictionary<string, IList<string>>();

    public void AddQueueMapping(Type messageType, string queue)
    {
        string key = messageType.FullName!;
        ((ConcurrentDictionary<string, IList<string>>)QueueMappings).AddOrUpdate(
            key,
            _ => new List<string> { queue },
            (_, existing) => { if (!existing.Contains(queue)) existing.Add(queue); return existing; });
    }

    public void AddQueueMapping(Type messageType, IList<string> queues)
    {
        string key = messageType.FullName!;
        ((ConcurrentDictionary<string, IList<string>>)QueueMappings).AddOrUpdate(
            key,
            _ => new List<string>(queues),
            (_, existing) => { foreach (var q in queues) { if (!existing.Contains(q)) existing.Add(q); } return existing; });
    }
}
