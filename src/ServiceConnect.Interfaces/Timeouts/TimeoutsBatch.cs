namespace ServiceConnect.Interfaces;

/// <summary>
/// Represents a batch of due timeouts returned from a timeout store query.
/// </summary>
public sealed class TimeoutsBatch
{
    /// <summary>
    /// Gets the timeouts that are ready to be triggered.
    /// Producers assign once at construction via init; consumers read only.
    /// </summary>
    public IReadOnlyList<TimeoutData> DueTimeouts { get; init; } = [];
}
