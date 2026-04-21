namespace ServiceConnect.Interfaces.Exceptions;

/// <summary>
/// Represents a persistence-layer failure raised by a ServiceConnect storage provider.
/// </summary>
public sealed class PersistenceException : ServiceConnectException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PersistenceException"/> class.
    /// </summary>
    public PersistenceException() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="PersistenceException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public PersistenceException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="PersistenceException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The underlying provider exception, if any.</param>
    public PersistenceException(string message, Exception? innerException) : base(message, innerException) { }
}
