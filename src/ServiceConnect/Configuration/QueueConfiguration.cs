using System.Collections.Concurrent;
using System.Collections.Immutable;
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

    // Keyed by message-type FullName. Values are immutable lists; updates use
    // compare-and-swap (AddOrUpdate) so additions from concurrent builders are
    // safe without an outer lock (C-04).
    private readonly ConcurrentDictionary<string, ImmutableList<string>> _queueMappings = new();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> QueueMappings =>
        new QueueMappingsView(_queueMappings);

    public void AddQueueMapping(Type messageType, string queue)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        if (string.IsNullOrWhiteSpace(queue))
            throw new ArgumentException("Queue must be a non-empty string.", nameof(queue));

        string key = messageType.FullName!;
        _queueMappings.AddOrUpdate(
            key,
            _ => ImmutableList.Create(queue),
            (_, existing) => existing.Contains(queue) ? existing : existing.Add(queue));
    }

    public void AddQueueMapping(Type messageType, IList<string> queues)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(queues);
        string key = messageType.FullName!;
        _queueMappings.AddOrUpdate(
            key,
            _ => ImmutableList.CreateRange(queues),
            (_, existing) =>
            {
                var updated = existing;
                foreach (var q in queues)
                {
                    if (!updated.Contains(q)) updated = updated.Add(q);
                }
                return updated;
            });
    }

    public bool TryGetQueueMapping(Type messageType, out IReadOnlyList<string> queues)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        if (_queueMappings.TryGetValue(messageType.FullName!, out var list))
        {
            queues = list;
            return true;
        }
        queues = Array.Empty<string>();
        return false;
    }

    private sealed class QueueMappingsView : IReadOnlyDictionary<string, IReadOnlyList<string>>
    {
        private readonly ConcurrentDictionary<string, ImmutableList<string>> _source;
        public QueueMappingsView(ConcurrentDictionary<string, ImmutableList<string>> source) => _source = source;

        public IReadOnlyList<string> this[string key] => _source[key];
        public IEnumerable<string> Keys => _source.Keys;
        public IEnumerable<IReadOnlyList<string>> Values => _source.Values;
        public int Count => _source.Count;
        public bool ContainsKey(string key) => _source.ContainsKey(key);
        public bool TryGetValue(string key, out IReadOnlyList<string> value)
        {
            if (_source.TryGetValue(key, out var list))
            {
                value = list;
                return true;
            }
            value = Array.Empty<string>();
            return false;
        }
        public IEnumerator<KeyValuePair<string, IReadOnlyList<string>>> GetEnumerator()
        {
            foreach (var kvp in _source)
                yield return new KeyValuePair<string, IReadOnlyList<string>>(kvp.Key, kvp.Value);
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
