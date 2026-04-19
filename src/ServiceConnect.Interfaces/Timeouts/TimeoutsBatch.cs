namespace ServiceConnect.Interfaces;

/// <summary>
/// Represents a batch of due timeouts returned from a timeout store query.
/// </summary>
public sealed class TimeoutsBatch
{
    /// <summary>
    /// Gets or sets the timeouts that are ready to be triggered.
    /// </summary>
    public IList<TimeoutData> DueTimeouts { get; set; } = [];

    /// <summary>
    /// Gets or sets the next recommended time to query the persistence store for due timeouts.
    /// </summary>
    public DateTimeOffset NextQueryTime { get; set; }
}
