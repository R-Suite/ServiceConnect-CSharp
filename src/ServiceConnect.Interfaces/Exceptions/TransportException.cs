namespace ServiceConnect.Interfaces.Exceptions;

/// <summary>
/// Represents a transport-layer failure when sending, publishing, or consuming messages.
/// </summary>
public sealed class TransportException : ServiceConnectException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TransportException"/> class.
    /// </summary>
    public TransportException() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="TransportException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public TransportException(string message) : base(message) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="TransportException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The underlying transport exception, if any.</param>
    public TransportException(string message, Exception? innerException) : base(message, innerException) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="TransportException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="endpoint">The affected endpoint, if known.</param>
    /// <param name="innerException">The underlying transport exception, if any.</param>
    public TransportException(string message, string? endpoint, Exception? innerException = null)
        : base(message, innerException)
    {
        Endpoint = endpoint;
    }

    /// <summary>
    /// Gets the endpoint involved in the transport failure, if known.
    /// </summary>
    public string? Endpoint { get; }
}
