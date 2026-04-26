namespace ServiceConnect.Interfaces;

/// <summary>
/// Produces messages to the message broker.
/// </summary>
public interface IProducer : IAsyncDisposable
{
    /// <summary>
    /// Publishes a serialized message to all subscribers of the specified type.
    /// </summary>
    Task PublishAsync(Type type, byte[] message, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a serialized message to the configured queue for the specified type.
    /// </summary>
    Task SendAsync(Type type, byte[] message, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a serialized message to a specific endpoint.
    /// </summary>
    Task SendAsync(string endPoint, Type type, byte[] message, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends raw bytes to a specific endpoint without type-based routing. The <paramref name="type"/>
    /// is the logical message type the packet represents (for example, the element type of a stream);
    /// it is used to stamp transport-reserved type headers authoritatively.
    /// </summary>
    Task SendBytesAsync(string endPoint, Type type, byte[] packet, IDictionary<string, string>? headers = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the maximum message size in bytes supported by the broker.
    /// </summary>
    long MaximumMessageSize { get; }

    /// <summary>
    /// Disconnects the producer from the broker.
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
