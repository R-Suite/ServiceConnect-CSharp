namespace ServiceConnect.Interfaces.Exceptions;

public class PersistenceException : ServiceConnectException
{
    public PersistenceException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
