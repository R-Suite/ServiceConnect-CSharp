namespace ServiceConnect.Interfaces.Exceptions;

public class SerializationException : ServiceConnectException
{
    public Type? MessageType { get; }
    public SerializationException(string message, Type? messageType = null, Exception? innerException = null)
        : base(message, innerException)
    {
        MessageType = messageType;
    }
}
