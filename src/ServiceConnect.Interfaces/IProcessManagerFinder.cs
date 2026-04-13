namespace ServiceConnect.Interfaces;

public delegate void TimeoutInsertedDelegate(DateTime timeoutTime);

public interface IProcessManagerFinder
{
    Task<IPersistenceData<T>?> FindDataAsync<T>(IProcessManagerPropertyMapper mapper, Message message, CancellationToken cancellationToken = default) where T : class, IProcessManagerData;
    Task InsertDataAsync(IProcessManagerData data, CancellationToken cancellationToken = default);
    Task UpdateDataAsync<T>(IPersistenceData<T> data, CancellationToken cancellationToken = default) where T : class, IProcessManagerData;
    Task DeleteDataAsync<T>(IPersistenceData<T> data, CancellationToken cancellationToken = default) where T : class, IProcessManagerData;
}
