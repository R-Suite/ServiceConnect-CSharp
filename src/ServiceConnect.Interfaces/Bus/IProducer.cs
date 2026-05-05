namespace ServiceConnect.Interfaces;

/// <summary>
/// Produces messages to the message broker.
/// </summary>
public interface IProducer : IAsyncDisposable
{
    /// <summary>
    /// Publishes a serialized message to all subscribers of the specified type.
    /// </summary>
    /// <param name="type">The logical message type.</param>
    /// <param name="body">The serialized message body.</param>
    /// <param name="headers">Optional read-only headers to include with the message.</param>
    /// <param name="cancellationToken">A token used to cancel the publish operation.</param>
    Task PublishAsync(Type type, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a serialized message to the configured queue for the specified type.
    /// </summary>
    /// <param name="type">The logical message type used to resolve destination queues.</param>
    /// <param name="body">The serialized message body.</param>
    /// <param name="headers">Optional read-only headers to include with the message.</param>
    /// <param name="cancellationToken">A token used to cancel the send operation.</param>
    /// <remarks>
    /// When the message type maps to multiple queues, every endpoint is attempted; per-endpoint
    /// failures are collected and surface as an <see cref="AggregateException"/> at the end of
    /// the loop. Cancellation via <paramref name="cancellationToken"/> propagates as
    /// <see cref="OperationCanceledException"/> directly and aborts the remaining iterations.
    /// </remarks>
    Task SendAsync(Type type, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a serialized message to a specific endpoint.
    /// </summary>
    /// <param name="endPoint">The destination queue name.</param>
    /// <param name="type">The logical message type used when stamping headers.</param>
    /// <param name="body">The serialized message body.</param>
    /// <param name="headers">Optional read-only headers to include with the message.</param>
    /// <param name="cancellationToken">A token used to cancel the send operation.</param>
    Task SendAsync(string endPoint, Type type, ReadOnlyMemory<byte> body, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends raw bytes to a specific endpoint without type-based routing. The <paramref name="type"/>
    /// is the logical message type the packet represents (for example, the element type of a stream);
    /// it is used to stamp transport-reserved type headers authoritatively.
    /// </summary>
    /// <param name="endPoint">The destination queue name.</param>
    /// <param name="type">The logical message type the packet represents.</param>
    /// <param name="packet">The raw payload to send.</param>
    /// <param name="headers">Optional read-only headers to include with the packet.</param>
    /// <param name="cancellationToken">A token used to cancel the send operation.</param>
    Task SendBytesAsync(string endPoint, Type type, ReadOnlyMemory<byte> packet, IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default);

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
    /// Gets whether the producer has attempted at least one connection to the broker.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> for a freshly-constructed producer that has not
    /// yet been asked to publish or send. Once a publish/send call begins (whether or
    /// not it succeeds), this becomes <see langword="true"/> and stays <see langword="true"/>
    /// for the producer's lifetime. The producer health check uses this to distinguish
    /// "lazy, not yet tried" (Healthy) from "tried and currently disconnected" (Unhealthy).
    /// </remarks>
    bool HasAttemptedConnection { get; }

    /// <summary>
    /// Disconnects the producer from the broker.
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
