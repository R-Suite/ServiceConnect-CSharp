namespace ServiceConnect.Interfaces;

/// <summary>
/// Base type for transport messages.
/// </summary>
/// <remarks>
/// Application messages are expected to inherit from this type so ServiceConnect can
/// flow a correlation identifier consistently across send, publish, request/reply,
/// and process-manager operations.
/// </remarks>
public class Message(Guid correlationId)
{
    /// <summary>
    /// Gets the correlation id used to relate this message to a broader conversation.
    /// </summary>
    public Guid CorrelationId { get; private set; } = correlationId;
}
