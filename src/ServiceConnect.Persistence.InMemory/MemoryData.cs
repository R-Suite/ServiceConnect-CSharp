using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Wraps persisted process manager data with an identifier and version.
/// </summary>
public sealed class MemoryData<T> : IPersistenceData<T>, IVersioned where T : class, IProcessManagerData
{
    /// <summary>
    /// Gets or sets the storage identifier for this entry.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the optimistic concurrency version for this entry.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Gets or sets the process manager data payload.
    /// </summary>
    public required T Data { get; set; }
}
