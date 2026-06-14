namespace ServiceConnect.Interfaces.Exceptions;

/// <summary>
/// Thrown by a persistence implementation when an optimistic-concurrency update fails
/// because another writer modified the same aggregate between read and write.
/// Callers should typically retry the full read-modify-write cycle on a new snapshot.
/// </summary>
public sealed class ConcurrencyException : ServiceConnectException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrencyException"/> class.
    /// </summary>
    public ConcurrencyException() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrencyException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public ConcurrencyException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrencyException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The underlying cause of the exception.</param>
    public ConcurrencyException(string message, Exception? innerException) : base(message, innerException) { }
}
