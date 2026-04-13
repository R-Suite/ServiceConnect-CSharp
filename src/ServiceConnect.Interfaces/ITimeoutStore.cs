namespace ServiceConnect.Interfaces;

public interface ITimeoutStore
{
    event TimeoutInsertedDelegate? TimeoutInserted;
    Task InsertTimeoutAsync(TimeoutData timeoutData, CancellationToken cancellationToken = default);
    Task<TimeoutsBatch> GetTimeoutsBatchAsync(CancellationToken cancellationToken = default);
    Task RemoveDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default);
}
