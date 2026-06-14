namespace ServiceConnect.Interfaces;

/// <summary>
/// Describes a persisted aggregator snapshot, including any records that could not be resolved.
/// </summary>
public interface IAggregatorSnapshot
{
    /// <summary>
    /// Gets the stored messages that were successfully resolved back into CLR objects.
    /// </summary>
    IReadOnlyList<IHasCorrelationId> ResolvedMessages { get; }

    /// <summary>
    /// Gets the storage ids for the resolved messages.
    /// </summary>
    IReadOnlyList<Guid> ResolvedIds { get; }

    /// <summary>
    /// Gets the number of stored records that could not be resolved.
    /// </summary>
    int UnresolvedCount { get; }
}
