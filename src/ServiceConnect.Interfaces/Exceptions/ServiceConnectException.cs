namespace ServiceConnect.Interfaces.Exceptions;

public class ServiceConnectException : Exception
{
    public ServiceConnectException() { }
    public ServiceConnectException(string message) : base(message) { }
    public ServiceConnectException(string message, Exception? innerException) : base(message, innerException) { }
}
