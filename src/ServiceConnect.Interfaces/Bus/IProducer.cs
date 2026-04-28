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
    /// Gets whether the producer is currently connected and ready to publish or send.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> before the first publish/send call (the producer
    /// connects lazily) and after a connection drop until the next reconnect. Mirrors
    /// <see cref="IConsumer.IsConnected"/>.
    /// </remarks>
    bool IsHealthy { get; }

    /// <summary>
    /// Disconnects the producer from the broker.
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
