using System;

namespace R.MessageBus.Interfaces
{
    public interface IProcessManagerFinder
    {
        IPersistanceData<T> NewData<T>() where T : class, IProcessManagerData;
        IPersistanceData<T> FindData<T>(Guid id) where T : class, IProcessManagerData;
        void InsertData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData;
        void UpdateData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData;
        void DeleteData<T>(IPersistanceData<T> data) where T : class, IProcessManagerData;
    }
}
