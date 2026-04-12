namespace ServiceConnect.Interfaces.Exceptions;

public sealed class PersistenceException(string message, Exception? innerException = null)
    : ServiceConnectException(message, innerException);
