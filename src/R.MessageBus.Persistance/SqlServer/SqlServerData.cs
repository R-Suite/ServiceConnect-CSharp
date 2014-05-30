using R.MessageBus.Interfaces;

namespace R.MessageBus.Persistance.SqlServer
{
    public class SqlServerData<T> : IPersistanceData<T>
    {
        public T Data { get; set; }
    }
}
