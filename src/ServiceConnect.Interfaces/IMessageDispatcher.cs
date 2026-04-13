namespace ServiceConnect.Interfaces;

/// <summary>
/// Dispatches incoming messages to the appropriate handler.
/// </summary>
public interface IMessageDispatcher
{
    /// <summary>
    /// Deserializes and dispatches a message to its registered handler.
    /// </summary>
    Task<ConsumeEventResult> Dispatch(byte[] messageBytes, string messageType, IDictionary<string, object> headers, CancellationToken cancellationToken = default);
}
