namespace ServiceConnect.Interfaces;

public class Message(Guid correlationId)
{
    public Guid CorrelationId { get; private set; } = correlationId;
}
