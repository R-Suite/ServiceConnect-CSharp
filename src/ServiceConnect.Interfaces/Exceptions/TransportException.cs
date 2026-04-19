namespace ServiceConnect.Interfaces.Exceptions;

/// <summary>
/// Represents a transport-layer failure when sending, publishing, or consuming messages.
/// </summary>
/// <param name="message">The exception message.</param>
/// <param name="endpoint">The affected endpoint, if known.</param>
/// <param name="innerException">The underlying transport exception, if any.</param>
public sealed class TransportException(string message, string? endpoint = null, Exception? innerException = null)
    : ServiceConnectException(message, innerException)
{
    /// <summary>
    /// Gets the endpoint involved in the transport failure, if known.
    /// </summary>
    public string? Endpoint { get; } = endpoint;
}
