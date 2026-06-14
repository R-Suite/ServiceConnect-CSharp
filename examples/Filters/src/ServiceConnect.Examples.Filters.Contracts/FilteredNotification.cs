using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.Filters.Contracts;

public sealed class FilteredNotification(Guid correlationId) : Message(correlationId)
{
    public string MessageText { get; init; } = string.Empty;
}
