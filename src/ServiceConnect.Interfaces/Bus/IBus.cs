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
    /// <remarks>
    /// Parameter order differs from <see cref="PublishAsync"/>/<see cref="SendAsync"/>
    /// (which put <c>options</c> second): <paramref name="onReply"/> is required and C#
    /// does not allow an optional parameter (<c>options</c>) to precede a required one,
    /// so the callback must come second. The alternative — making <c>options</c>
    /// required — would force every caller to pass <see cref="RequestOptions.Default"/>
    /// explicitly, which is worse ergonomics than the position asymmetry.
    /// </remarks>
    Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where TRequest : Message where TReply : Message;

    /// <summary>
    /// Routes a message through a series of destinations using a routing slip.
    /// </summary>
    Task RouteAsync<T>(T message, IList<string> destinations, CancellationToken cancellationToken = default) where T : Message;

    /// <summary>
    /// Creates a streaming connection for sending large messages in chunks.
    /// </summary>
    IMessageBusWriteStream CreateStream<T>(string endpoint) where T : Message;

    /// <summary>
    /// Starts consuming messages from the configured queue.
    /// <para>
    /// Throws <see cref="InvalidOperationException"/> if the bus is already consuming,
    /// or if the bus has previously been stopped — stop is terminal, so consumers must
    /// dispose the bus and create a new instance to resume consumption.
    /// </para>
    /// </summary>
    Task StartConsumingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops consuming messages and disposes the underlying consumer.
    /// <para>
    /// This operation is <b>terminal</b>: once stopped, the bus cannot be restarted.
    /// <see cref="StartConsumingAsync"/> will throw <see cref="InvalidOperationException"/>.
    /// To resume consumption, dispose this bus and create a new instance.
    /// </para>
    /// </summary>
    Task StopConsumingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets whether the bus is currently consuming messages.
    /// </summary>
    bool IsConsuming { get; }

    /// <summary>
    /// Schedules a <see cref="TimeoutMessage"/> to be delivered to the current queue
    /// after the specified delay. The message's <c>CorrelationId</c> will equal
    /// <paramref name="correlationId"/>, which is the standard key for Process
    /// Manager correlation.
    /// </summary>
    Task RequestTimeoutAsync(Guid correlationId, TimeSpan delay, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This IBus implementation does not support scheduling timeouts.");
}
