namespace ServiceConnect.Interfaces.Exceptions;

/// <summary>
/// Represents a failure to serialize or deserialize a message payload.
/// </summary>
/// <param name="message">The exception message.</param>
/// <param name="messageType">The message type involved in the failure, if known.</param>
/// <param name="innerException">The underlying serializer exception, if any.</param>
public sealed class SerializationException(string message, Type? messageType = null, Exception? innerException = null)
    : ServiceConnectException(message, innerException)
{
    /// <summary>
    /// Gets the message type involved in the serialization failure, when available.
    /// </summary>
    public Type? MessageType { get; } = messageType;
}
