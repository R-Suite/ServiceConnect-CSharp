using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.ScatterGather.Contracts;

public sealed class SearchResponse(Guid correlationId) : Message(correlationId)
{
    public string CatalogName { get; init; } = string.Empty;

    public string ResultId { get; init; } = string.Empty;
}
