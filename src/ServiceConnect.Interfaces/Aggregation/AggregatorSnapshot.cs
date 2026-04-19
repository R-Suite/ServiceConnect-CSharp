namespace ServiceConnect.Interfaces;

/// <summary>
/// A point-in-time capture of aggregator messages returned by
/// <see cref="IAggregatorPersistor.GetSnapshotAsync(string,System.Threading.CancellationToken)"/>.
/// Carries the deserialised messages, the ids of the underlying storage records,
/// and the count of records that could not be resolved (e.g. renamed CLR types).
/// </summary>
public sealed record AggregatorSnapshot(
    IReadOnlyList<object> ResolvedMessages,
    IReadOnlyList<Guid> ResolvedIds,
    int UnresolvedCount) : IAggregatorSnapshot
{
    /// <summary>
    /// Gets an empty snapshot with no resolved or unresolved records.
    /// </summary>
    public static AggregatorSnapshot Empty { get; } = new([], [], 0);
}
