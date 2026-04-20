using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.RequestReply.Contracts;

public sealed class QuoteResponse(Guid correlationId) : Message(correlationId)
{
    public decimal Price { get; init; }
}