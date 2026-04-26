using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistence.InMemory;

internal sealed record TimeoutEntry(DateTimeOffset Time, Guid Id, TimeoutData Data);
