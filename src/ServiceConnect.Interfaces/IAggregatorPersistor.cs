namespace ServiceConnect.Interfaces;

public interface IAggregatorPersistor
{
    Task InsertDataAsync(object data, string name, CancellationToken cancellationToken = default);
    Task<IList<object>> GetDataAsync(string name, CancellationToken cancellationToken = default);
    Task RemoveDataAsync(string name, Guid correlationId, CancellationToken cancellationToken = default);
    Task RemoveAllAsync(string name, CancellationToken cancellationToken = default);
    Task<int> CountAsync(string name, CancellationToken cancellationToken = default);
}
