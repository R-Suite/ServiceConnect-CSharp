using System;
using R.MessageBus.Interfaces;

namespace R.MessageBus.Persistance.InMemory
{
    public class MemoryData<T> : IPersistanceData<T>
    {
        public Guid Id { get; set; }
        public int Version { get; set; }
        public T Data { get; set; }
    }
}
