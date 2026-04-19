namespace ServiceConnect.Interfaces.Configuration;

/// <summary>
/// Configures queue names and message-to-queue routing.
/// </summary>
public interface IQueueConfiguration
{
    /// <summary>
    /// Gets or sets the primary queue name used by the bus.
    /// </summary>
    string QueueName { get; set; }

    /// <summary>
    /// Gets or sets the queue name used for failed messages.
    /// </summary>
    string ErrorQueueName { get; set; }

    /// <summary>
    /// Gets or sets the queue name used for audit copies.
    /// </summary>
    string AuditQueueName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether message auditing is enabled.
    /// </summary>
    bool AuditingEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether failed messages bypass the error queue.
    /// </summary>
    bool DisableErrors { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the main queue is purged during startup.
    /// </summary>
    bool PurgeQueueOnStartup { get; set; }

    /// <summary>
    /// Gets the configured message-to-queue routing table.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> QueueMappings { get; }

    /// <summary>
    /// Adds a single queue mapping for the specified message type.
    /// </summary>
    /// <param name="messageType">The message type to route.</param>
    /// <param name="queue">The destination queue name.</param>
    void AddQueueMapping(Type messageType, string queue);

    /// <summary>
    /// Adds multiple queue mappings for the specified message type.
    /// </summary>
    /// <param name="messageType">The message type to route.</param>
    /// <param name="queues">The destination queue names.</param>
    void AddQueueMapping(Type messageType, IList<string> queues);

    /// <summary>
    /// Attempts to resolve the configured queue mappings for a message type.
    /// </summary>
    /// <param name="messageType">The message type to look up.</param>
    /// <param name="queues">When this method returns, contains the configured queues if a mapping exists.</param>
    /// <returns><see langword="true"/> when a mapping exists; otherwise <see langword="false"/>.</returns>
    bool TryGetQueueMapping(Type messageType, out IReadOnlyList<string> queues);
}
