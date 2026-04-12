namespace ServiceConnect.Interfaces;

public delegate void TimeoutInsertedDelegate(DateTime timeoutTime);

public interface IProcessManagerFinder
{
    IPersistenceData<T>? FindData<T>(IProcessManagerPropertyMapper mapper, Message message) where T : class, IProcessManagerData;
    void InsertData(IProcessManagerData data);
    void UpdateData<T>(IPersistenceData<T> data) where T : class, IProcessManagerData;
    void DeleteData<T>(IPersistenceData<T> data) where T : class, IProcessManagerData;
}
