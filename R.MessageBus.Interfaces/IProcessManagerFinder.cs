using System;

namespace R.MessageBus.Interfaces
{
    public interface IProcessManagerFinder
    {
        VersionData<T> FindData<T>(Guid id) where T : class, IProcessManagerData;

        void InsertData<T>(VersionData<T> data) where T : IProcessManagerData;

        void UpdateData<T>(VersionData<T> data) where T : IProcessManagerData;

        void DeleteData<T>(VersionData<T> data) where T : IProcessManagerData;
    }
}
