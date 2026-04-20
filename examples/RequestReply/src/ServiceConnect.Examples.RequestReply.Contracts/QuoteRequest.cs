using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.RequestReply.Contracts;

public sealed class QuoteRequest(Guid correlationId) : Message(correlationId)
{
    public string ProductCode { get; init; } = string.Empty;
}