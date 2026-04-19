namespace ServiceConnect.Interfaces;

public interface IAggregatorPersistor
{
    Task InsertDataAsync(object data, string name, CancellationToken cancellationToken = default);
    Task<IList<object>> GetDataAsync(string name, CancellationToken cancellationToken = default);
    Task<IAggregatorSnapshot> GetSnapshotAsync(string name, CancellationToken cancellationToken = default);
    Task RemoveDataAsync(string name, Guid correlationId, CancellationToken cancellationToken = default);
    Task RemoveAllAsync(string name, CancellationToken cancellationToken = default);
    Task RemoveSnapshotAsync(string name, IAggregatorSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<int> CountAsync(string name, CancellationToken cancellationToken = default);
}

public interface IAggregatorSnapshot
{
    IReadOnlyList<object> ResolvedMessages { get; }
    IReadOnlyList<Guid> ResolvedIds { get; }
    int UnresolvedCount { get; }
}
