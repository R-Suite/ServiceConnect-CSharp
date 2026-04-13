using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Interfaces;

/// <summary>
/// The core message bus interface for publishing, sending, and consuming messages.
/// </summary>
public interface IBus : IAsyncDisposable
{
    /// <summary>
    /// Publishes a message to all subscribers of the message type.
    /// </summary>
    Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : Message;

    /// <summary>
    /// Sends a message to a specific endpoint or to the configured queue mapping.
    /// </summary>
    Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message;

    /// <summary>
    /// Sends a request and waits for a single reply.
    /// </summary>
    Task<TReply> SendRequestAsync<T, TReply>(T message, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where T : Message where TReply : Message;

    /// <summary>
    /// Sends a request and waits for multiple replies from all respondents.
    /// </summary>
    Task<IList<TReply>> SendRequestMultiAsync<T, TReply>(T message, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where T : Message where TReply : Message;

    /// <summary>
    /// Publishes a request and invokes a callback for each reply received.
    /// </summary>
    Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where TRequest : Message where TReply : Message;

    /// <summary>
    /// Routes a message through a series of destinations using a routing slip.
    /// </summary>
    Task RouteAsync<T>(T message, IList<string> destinations, CancellationToken cancellationToken = default) where T : Message;

    /// <summary>
    /// Creates a streaming connection for sending large messages in chunks.
    /// </summary>
    IMessageBusWriteStream CreateStream<T>(string endpoint, T message) where T : Message;

    /// <summary>
    /// Starts consuming messages from the configured queue.
    /// </summary>
    Task StartConsumingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops consuming messages and disposes the consumer.
    /// </summary>
    Task StopConsumingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets whether the bus is currently consuming messages.
    /// </summary>
    bool IsConnected { get; }
}
