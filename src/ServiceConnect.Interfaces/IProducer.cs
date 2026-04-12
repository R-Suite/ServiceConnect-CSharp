namespace ServiceConnect.Interfaces;

/// <summary>
/// Produces messages to the message broker.
/// </summary>
public interface IProducer : IAsyncDisposable
{
    /// <summary>
    /// Publishes a serialized message to all subscribers of the specified type.
    /// </summary>
    Task PublishAsync(Type type, byte[] message, Dictionary<string, string>? headers = null);

    /// <summary>
    /// Sends a serialized message to the configured queue for the specified type.
    /// </summary>
    Task SendAsync(Type type, byte[] message, Dictionary<string, string>? headers = null);

    /// <summary>
    /// Sends a serialized message to a specific endpoint.
    /// </summary>
    Task SendAsync(string endPoint, Type type, byte[] message, Dictionary<string, string>? headers = null);

    /// <summary>
    /// Sends raw bytes to a specific endpoint without type-based routing.
    /// </summary>
    Task SendBytesAsync(string endPoint, byte[] packet, Dictionary<string, string>? headers = null);

    /// <summary>
    /// Gets the maximum message size in bytes supported by the broker.
    /// </summary>
    long MaximumMessageSize { get; }

    /// <summary>
    /// Disconnects the producer from the broker.
    /// </summary>
    Task DisconnectAsync();
}
