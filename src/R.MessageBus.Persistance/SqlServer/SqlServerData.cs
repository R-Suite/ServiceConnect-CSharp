using System;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Persistance.SqlServer
{
    public class SqlServerData<T> : IPersistanceData<T>
    {
        public Guid Id { get; set; }

        public T Data { get; set; }
    }
}
