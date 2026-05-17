using System.Collections.Concurrent;
using System.Collections.Immutable;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

/// <summary>
/// Mutable implementation of <see cref="IQueueConfiguration"/> used to configure local queue names and routing maps.
/// </summary>
internal sealed class QueueConfiguration : IQueueConfiguration
{
    private bool _frozen;
    private string _queueName = "";
    private string _errorQueueName = "errors";
    private string _auditQueueName = "audit";
    private bool _auditingEnabled;
    private bool _disableErrors;
    private bool _purgeQueueOnStartup;

    /// <summary>
    /// Latches this configuration so further setter calls throw <see cref="InvalidOperationException"/>.
    /// Called by <see cref="BusConfiguration.Freeze"/> after the user's configure callback returns.
    /// </summary>
    internal void Freeze() => _frozen = true;

    private void ThrowIfFrozen([System.Runtime.CompilerServices.CallerMemberName] string? memberName = null)
    {
        if (_frozen)
        {
            throw new InvalidOperationException(
                $"QueueConfiguration is frozen — '{memberName}' cannot be modified after AddServiceConnect has returned. " +
                "Configure all properties inside the AddServiceConnect callback.");
        }
    }

    /// <inheritdoc />
    public string QueueName { get => _queueName; set { ThrowIfFrozen(); _queueName = value; } }
    /// <inheritdoc />
    public string ErrorQueueName { get => _errorQueueName; set { ThrowIfFrozen(); _errorQueueName = value; } }
    /// <inheritdoc />
    public string AuditQueueName { get => _auditQueueName; set { ThrowIfFrozen(); _auditQueueName = value; } }
    /// <inheritdoc />
    public bool AuditingEnabled { get => _auditingEnabled; set { ThrowIfFrozen(); _auditingEnabled = value; } }
    /// <inheritdoc />
    public bool DisableErrors { get => _disableErrors; set { ThrowIfFrozen(); _disableErrors = value; } }
    /// <inheritdoc />
    public bool PurgeQueueOnStartup { get => _purgeQueueOnStartup; set { ThrowIfFrozen(); _purgeQueueOnStartup = value; } }

    // Keyed by message-type AssemblyQualifiedName so two types sharing a FullName
    // (same namespace+name in different assemblies) don't collide into one bucket
    // and cross-wire each other's routing. The list preserves registration order
    // for callers while the set gives O(1) duplicate checks.
    private readonly ConcurrentDictionary<string, QueueMappingEntry> _queueMappings = new(StringComparer.Ordinal);

    // Cached wrapper so repeated reads of QueueMappings don't allocate a new object each time.
    // Nulled out after every mutation so the next read gets a fresh wrapper over the updated dictionary.
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _mappingsView;

    private static string GetMappingKey(Type messageType) =>
        messageType.AssemblyQualifiedName
            ?? throw new ArgumentException(
                $"Message type '{messageType}' has no AssemblyQualifiedName and cannot be used as a queue-mapping key.",
                nameof(messageType));

    /// <inheritdoc />
    /// <remarks>
    /// Cache invalidation on mutation is not synchronised; callers must not mutate
    /// (<see cref="AddQueueMapping(Type, string)"/> / <see cref="AddQueueMapping(Type, IReadOnlyList{string})"/>)
    /// concurrently with reads. Mappings are intended to be populated at startup and
    /// read at dispatch time.
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> QueueMappings =>
        _mappingsView ??= new QueueMappingsView(_queueMappings);

    /// <inheritdoc />
    public void AddQueueMapping(Type messageType, string queue)
    {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(messageType);
        if (string.IsNullOrWhiteSpace(queue))
        {
            throw new ArgumentException("Queue must be a non-empty string.", nameof(queue));
        }

        string key = GetMappingKey(messageType);
        _queueMappings.AddOrUpdate(
            key,
            _ => QueueMappingEntry.Create(queue),
            (_, existing) => existing.Contains(queue) ? existing : existing.Add(queue));
        _mappingsView = null;
    }

    /// <inheritdoc />
    public void AddQueueMapping(Type messageType, IReadOnlyList<string> queues)
    {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(queues);

        // Match the single-queue overload: reject null/empty/whitespace entries up
        // front so no empty-string or whitespace queue can be stored in the mapping.
        for (int i = 0; i < queues.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(queues[i]))
            {
                throw new ArgumentException(
                    $"Queue at index {i} must be a non-empty string.", nameof(queues));
            }
        }

        string key = GetMappingKey(messageType);
        _queueMappings.AddOrUpdate(
            key,
            _ => QueueMappingEntry.Create(queues),
            (_, existing) =>
            {
                var updated = existing;
                foreach (var q in queues)
                {
                    updated = updated.Add(q);
                }
                return updated;
            });
        _mappingsView = null;
    }

    /// <inheritdoc />
    public bool TryGetQueueMapping(Type messageType, out IReadOnlyList<string> queues)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        if (_queueMappings.TryGetValue(GetMappingKey(messageType), out var entry))
        {
            queues = entry.List;
            return true;
        }
        queues = [];
        return false;
    }

    private sealed class QueueMappingsView(ConcurrentDictionary<string, QueueConfiguration.QueueMappingEntry> source) : IReadOnlyDictionary<string, IReadOnlyList<string>>
    {
        private readonly ConcurrentDictionary<string, QueueMappingEntry> _source = source;

        public IReadOnlyList<string> this[string key] => _source[key].List;
        public IEnumerable<string> Keys => _source.Keys;
        public IEnumerable<IReadOnlyList<string>> Values => _source.Values.Select(entry => (IReadOnlyList<string>)entry.List);
        public int Count => _source.Count;
        public bool ContainsKey(string key) => _source.ContainsKey(key);
        public bool TryGetValue(string key, out IReadOnlyList<string> value)
        {
            if (_source.TryGetValue(key, out var entry))
            {
                value = entry.List;
                return true;
            }
            value = [];
            return false;
        }
        public IEnumerator<KeyValuePair<string, IReadOnlyList<string>>> GetEnumerator()
        {
            foreach (var kvp in _source)
            {
                yield return new KeyValuePair<string, IReadOnlyList<string>>(kvp.Key, kvp.Value.List);
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed record QueueMappingEntry(ImmutableList<string> List, ImmutableHashSet<string> Set)
    {
        public bool Contains(string queue) => Set.Contains(queue);

        public QueueMappingEntry Add(string queue) =>
            Contains(queue) ? this : new QueueMappingEntry(List.Add(queue), Set.Add(queue));

        public static QueueMappingEntry Create(string queue) =>
            new([queue], [queue]);

        public static QueueMappingEntry Create(IEnumerable<string> queues)
        {
            var list = ImmutableList<string>.Empty;
            var set = ImmutableHashSet<string>.Empty;

            foreach (var queue in queues)
            {
                if (set.Contains(queue))
                {
                    continue;
                }

                list = list.Add(queue);
                set = set.Add(queue);
            }

            return new QueueMappingEntry(list, set);
        }
    }
}
