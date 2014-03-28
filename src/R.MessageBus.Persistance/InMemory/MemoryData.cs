using R.MessageBus.Interfaces;

namespace R.MessageBus.Persistance.InMemory
{
    public class MemoryData<T> : IPersistanceData<T>
    {
        public T Data { get; set; }
    }
}