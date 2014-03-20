using System;

namespace R.MessageBus.Interfaces
{
    public interface IProcessManagerFinder
    {
        T FindData<T>(Guid id) where T : IProcessManagerData;

        void InsertData<T>(T data) where T : IProcessManagerData;

        void UpdateData<T>(T data) where T : IProcessManagerData;

        void DeleteData<T>(T data) where T : IProcessManagerData;
    }
}
