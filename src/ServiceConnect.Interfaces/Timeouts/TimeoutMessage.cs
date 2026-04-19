namespace ServiceConnect.Interfaces;

/// <summary>
/// Base message type used when dispatching scheduled process-manager timeouts.
/// </summary>
/// <param name="correlationId">The correlation id of the target process instance.</param>
public class TimeoutMessage(Guid correlationId) : Message(correlationId);
