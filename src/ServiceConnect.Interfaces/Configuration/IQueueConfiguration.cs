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
    /// Gets or sets a value indicating whether failed messages bypass the error topology.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/>, the consumer skips every publish to the error exchange:
    /// exhausted-retry messages, validator-rejected messages (missing type-name, oversized
    /// body, oversized headers), the no-handler dead-letter branch, AND the error-exchange
    /// fallback that normally runs when a retry-queue republish itself fails. In all of
    /// those cases the original delivery is acked and the message is dropped. Observability:
    /// the drop is surfaced on the <c>messaging.serviceconnect.retry.drops</c> counter with
    /// <c>error.type=errors-disabled</c> so operators can alert on the drop rate.
    /// <para>
    /// Auditing is orthogonal — gated independently by <see cref="AuditingEnabled"/> on the
    /// <see cref="AuditingEnabled"/> path. Setting <see cref="DisableErrors"/> to
    /// <see langword="true"/> does <b>not</b> suppress audit publishes.
    /// </para>
    /// <para>
    /// The retry-queue republish path is also unaffected — failed handlers are still
    /// republished to the per-queue retry queue with an incremented <c>RetryCount</c> header
    /// up to <c>ITransportConfiguration.MaxRetries</c>. Only the error-exchange terminal
    /// destination is disabled.
    /// </para>
    /// </remarks>
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
