namespace ServiceConnect.Interfaces;

public interface ITimeoutStore
{
    event TimeoutInsertedDelegate? TimeoutInserted;
    void InsertTimeout(TimeoutData timeoutData);
    TimeoutsBatch GetTimeoutsBatch();
    void RemoveDispatchedTimeout(Guid id);
}
