using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistence.InMemory;

public sealed class MemoryData<T> : IPersistenceData<T> where T : class, IProcessManagerData
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public T Data { get; set; } = default!;
}
