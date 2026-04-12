namespace ServiceConnect.Interfaces.Exceptions;

public sealed class SerializationException(string message, Type? messageType = null, Exception? innerException = null)
    : ServiceConnectException(message, innerException)
{
    public Type? MessageType { get; } = messageType;
}
