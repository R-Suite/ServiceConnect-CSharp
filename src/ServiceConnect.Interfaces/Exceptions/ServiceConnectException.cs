namespace ServiceConnect.Interfaces.Exceptions;

/// <summary>
/// Base type for exceptions raised by ServiceConnect.
/// </summary>
public abstract class ServiceConnectException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceConnectException"/> class.
    /// </summary>
    protected ServiceConnectException() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceConnectException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    protected ServiceConnectException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceConnectException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The underlying cause of the exception.</param>
    protected ServiceConnectException(string message, Exception? innerException) : base(message, innerException) { }
}
