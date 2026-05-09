using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Interfaces;

/// <summary>
/// The core message bus interface for publishing, sending, and consuming messages.
/// </summary>
/// <remarks>
/// <para>
/// <b>Delivery is at-least-once.</b> A handler may run more than once for the same logical
/// message — either because the broker redelivered it, or because the consumer did. Idempotency
/// is the consumer's responsibility.
/// </para>
/// <para>
/// <b>Persist-vs-ack gap.</b> When a handler returns successfully, the consumer dispatches an
/// acknowledgement to the broker. If the process crashes (or the broker fails over) between
/// handler success and the ack reaching durable broker state, the message redelivers on next
/// startup. Persistence writes (process-manager state, aggregator data, scheduled timeouts) are
/// completed before the ack — so a redelivered message hits a handler whose persisted state may
/// already reflect the prior run.
/// </para>
/// <para>
/// <b>Implication.</b> Either design handlers to be naturally idempotent (look up by a stable
/// business key, reconcile rather than overwrite), or build a per-consumer deduplication
/// filter pair (<c>BeforeConsuming</c> + <c>OnConsumedSuccessfully</c>) that records each
/// completed <c>MessageId</c> and short-circuits redeliveries.
/// </para>
/// </remarks>
public interface IBus : IAsyncDisposable
{
    /// <summary>
    /// Publishes a message to all subscribers of the message type.
    /// </summary>
    Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : Message;

    /// <summary>
    /// Sends a message to a specific endpoint or to the configured queue mapping.
    /// </summary>
    /// <remarks>
    /// When the message type maps to multiple queues (queue-mapping fan-out), every endpoint is
    /// attempted; per-endpoint failures are collected and surface as an
    /// <see cref="AggregateException"/>. Cancellation via <paramref name="cancellationToken"/>
    /// propagates as <see cref="OperationCanceledException"/> directly.
    /// </remarks>
    Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message;

    /// <summary>
    /// Sends a message to each of the specified endpoints. Each delivery is dispatched as a
    /// separate <see cref="SendAsync{T}"/>-equivalent call; failures on one endpoint do not
    /// abort the others — per-endpoint failures are collected and surface as an
    /// <see cref="AggregateException"/> at the end of the loop. Cancellation via
    /// <paramref name="cancellationToken"/> propagates as
    /// <see cref="OperationCanceledException"/> directly. The <c>options.EndPoint</c> field is
    /// ignored when this method is called — the explicit <paramref name="endPoints"/> parameter
    /// wins.
    /// </summary>
    Task SendToManyAsync<T>(T message, IReadOnlyList<string> endPoints, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message;

    /// <summary>
    /// Sends a request and waits for a single reply.
    /// </summary>
    Task<TReply> SendRequestAsync<TRequest, TReply>(TRequest message, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where TRequest : Message where TReply : Message;

    /// <summary>
    /// Sends a request and waits for multiple replies from all respondents.
    /// </summary>
    /// <remarks>
    /// <b>Under-delivery semantics (v8 behaviour change).</b> When
    /// <see cref="RequestOptions.ExpectedReplyCount"/> is a positive integer N, this call
    /// expects exactly N replies. If fewer than N arrive before
    /// <see cref="RequestOptions.Timeout"/> expires, the task throws
    /// <see cref="Exceptions.RequestTimeoutException"/>; the partials received before the
    /// timeout fired are exposed on
    /// <see cref="Exceptions.RequestTimeoutException.PartialReplies"/> so callers that want
    /// to recover them can. When <c>ExpectedReplyCount</c> is zero, negative, or null, no
    /// under-delivery check applies — the call returns every reply received during the
    /// window (the pre-v8 behaviour on every code path). Callers relying on the old
    /// silent-partial-return contract must catch <see cref="Exceptions.RequestTimeoutException"/>
    /// and read <see cref="Exceptions.RequestTimeoutException.PartialReplies"/>.
    /// </remarks>
    Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(TRequest message, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where TRequest : Message where TReply : Message;

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
    Task RouteAsync<T>(T message, IReadOnlyList<string> destinations, CancellationToken cancellationToken = default) where T : Message;

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
    /// Gets whether the bus is currently consuming messages. Returns <see langword="true"/> only
    /// when <see cref="StartConsumingAsync"/> has completed AND the broker has not cancelled
    /// the consumer. Broker-initiated <c>basic.cancel</c> events (queue deleted, policy expired,
    /// mirror promoted) flip this getter to <see langword="false"/> via
    /// <see cref="IConsumer.IsCancelledByBroker"/>; <c>BusConsumingHealthCheck</c> reports
    /// <c>Unhealthy</c> as a result.
    /// </summary>
    bool IsConsuming { get; }

    /// <summary>
    /// Gets whether the broker has cancelled the consumer (basic.cancel: queue deleted,
    /// policy expired, mirror promoted). Mirrors <see cref="IConsumer.IsCancelledByBroker"/>
    /// at the bus level so callers (e.g. <c>BusConsumingHealthCheck</c>) can distinguish a
    /// permanent broker-side failure from a transient connection flap. Default
    /// implementation returns <see langword="false"/>; framework-supplied buses override.
    /// </summary>
    bool IsCancelledByBroker => false;

    /// <summary>
    /// Schedules a <see cref="TimeoutMessage"/> to be delivered to the current queue
    /// after the specified delay. The message's <c>CorrelationId</c> will equal
    /// <paramref name="correlationId"/>, which is the standard key for Process
    /// Manager correlation.
    /// </summary>
    Task RequestTimeoutAsync(Guid correlationId, TimeSpan delay, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("This IBus implementation does not support scheduling timeouts."));
}
