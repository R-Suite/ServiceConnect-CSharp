namespace ServiceConnect.Interfaces.Exceptions;

/// <summary>
/// Represents a failure to serialize or deserialize a message payload.
/// </summary>
public sealed class SerializationException : ServiceConnectException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SerializationException"/> class.
    /// </summary>
    public SerializationException() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="SerializationException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public SerializationException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="SerializationException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The underlying serializer exception, if any.</param>
    public SerializationException(string message, Exception? innerException) : base(message, innerException) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="SerializationException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="messageType">The message type involved in the failure, if known.</param>
    /// <param name="innerException">The underlying serializer exception, if any.</param>
    public SerializationException(string message, Type? messageType, Exception? innerException = null)
        : base(message, innerException)
    {
        MessageType = messageType;
    }

    /// <summary>
    /// Gets the message type involved in the serialization failure, when available.
    /// </summary>
    public Type? MessageType { get; }
}
