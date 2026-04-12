namespace ServiceConnect.Interfaces.Exceptions;

public sealed class TransportException(string message, string? endpoint = null, Exception? innerException = null)
    : ServiceConnectException(message, innerException)
{
    public string? Endpoint { get; } = endpoint;
}
