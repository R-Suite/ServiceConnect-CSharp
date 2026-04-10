namespace ServiceConnect.Interfaces.Exceptions;

public class TransportException : ServiceConnectException
{
    public string? Endpoint { get; }
    public TransportException(string message, string? endpoint = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Endpoint = endpoint;
    }
}
