namespace ServiceConnect.Interfaces;

public class TimeoutMessage(Guid correlationId) : Message(correlationId);
