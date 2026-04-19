namespace ServiceConnect.Interfaces.Exceptions;

/// <summary>
/// Represents a persistence-layer failure raised by a ServiceConnect storage provider.
/// </summary>
/// <param name="message">The exception message.</param>
/// <param name="innerException">The underlying provider exception, if any.</param>
public sealed class PersistenceException(string message, Exception? innerException = null)
    : ServiceConnectException(message, innerException);
