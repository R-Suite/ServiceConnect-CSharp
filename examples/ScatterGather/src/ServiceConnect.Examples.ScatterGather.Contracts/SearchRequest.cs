using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.ScatterGather.Contracts;

public sealed class SearchRequest(Guid correlationId) : Message(correlationId)
{
    public string Query { get; init; } = string.Empty;
}
