namespace ServiceConnect.Interfaces;

/// <summary>
/// Holds timeout information.
/// </summary>
public sealed class TimeoutData
{
    /// <summary>
    /// Timeout id
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The address of the client who requested the timeout.
    /// </summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>
    /// The saga ID.
    /// </summary>
    public Guid ProcessManagerId { get; set; }

    /// <summary>
    /// The time at which the timeout expires.
    /// </summary>
    public DateTimeOffset Time { get; set; }

    /// <summary>
    /// Store the headers to preserve them across timeouts.
    /// </summary>
    public IDictionary<string, object> Headers { get; set; } = new Dictionary<string, object>(StringComparer.Ordinal);

    /// <summary>
    /// Mark processed timeouts as dispatched to prevent multiple dispatch of the same timeout
    /// </summary>
    public bool Locked { get; set; }

    /// <summary>
    /// When <see cref="Locked"/> is set by a polling consumer, this holds the
    /// unique poll-session id of the consumer that did the locking. Readers
    /// filter on this so a batch read sees only its own locked rows, not rows
    /// locked by a concurrent consumer.
    /// </summary>
    public Guid LockedBy { get; set; }

    /// <summary>
    /// When set, indicates when the current dispatch lock lease expires and the
    /// timeout may be reclaimed by another poller.
    /// </summary>
    public DateTimeOffset? LockExpiresAt { get; set; }
}
