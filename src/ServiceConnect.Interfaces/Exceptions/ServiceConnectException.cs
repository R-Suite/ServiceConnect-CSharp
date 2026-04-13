namespace ServiceConnect.Interfaces.Exceptions;

public abstract class ServiceConnectException : Exception
{
    protected ServiceConnectException() { }
    protected ServiceConnectException(string message) : base(message) { }
    protected ServiceConnectException(string message, Exception? innerException) : base(message, innerException) { }
}
