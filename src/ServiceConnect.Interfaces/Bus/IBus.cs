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
    /// <remarks>
    /// An outgoing filter returning <see cref="FilterAction.Stop"/> causes this call to
    /// return silently — the message is dropped before transport publish. No exception is thrown.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
    Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : Message;

    /// <summary>
    /// Sends a message to a specific endpoint or to the configured queue mapping.
    /// </summary>
    /// <remarks>
    /// When the message type maps to multiple queues (queue-mapping fan-out), every endpoint is
    /// attempted; per-endpoint failures are collected and surface as an
    /// <see cref="AggregateException"/>. Cancellation via <paramref name="cancellationToken"/>
    /// propagates as <see cref="OperationCanceledException"/> directly when no prior endpoint
    /// has failed; on multi-endpoint fan-out with prior failures, the OCE is wrapped as the
    /// first inner exception of an <see cref="AggregateException"/> that also carries the prior
    /// endpoint failures (so callers see both the cancellation and the partial-fan-out failures).
    /// An outgoing filter returning <see cref="FilterAction.Stop"/> causes this call
    /// to return silently — the message is dropped before transport publish. No exception is
    /// thrown.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
    Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message;

    /// <summary>
    /// Sends a message to each of the specified endpoints. Each delivery is dispatched as a
    /// separate <see cref="SendAsync{T}"/>-equivalent call; failures on one endpoint do not
    /// abort the others — per-endpoint failures are collected and surface as an
    /// <see cref="AggregateException"/> at the end of the loop. Cancellation via
    /// <paramref name="cancellationToken"/> propagates as
    /// <see cref="OperationCanceledException"/> directly when no prior endpoint has failed;
    /// when one or more prior endpoints have already failed, the cancellation surfaces as an
    /// <see cref="AggregateException"/> whose first inner exception is the
    /// <see cref="OperationCanceledException"/> and whose remaining inner exceptions are the
    /// prior endpoint failures, so callers see both the cancellation and the failures that
    /// preceded it. The <c>options.EndPoint</c> field is ignored when this method is called —
    /// the explicit <paramref name="endPoints"/> parameter wins.
    /// </summary>
    /// <remarks>
    /// An outgoing filter returning <see cref="FilterAction.Stop"/> causes this call
    /// to return silently — the message is dropped before any transport publish.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> or <paramref name="endPoints"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="endPoints"/> is empty.</exception>
    /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
    Task SendToManyAsync<T>(T message, IReadOnlyList<string> endPoints, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message;

    /// <summary>
    /// Sends a request and waits for a single reply.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><c>options.Timeout</c> is negative (other than <see cref="System.Threading.Timeout.Infinite"/>) or zero (likely <c>default(RequestOptions)</c>).</exception>
    /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
    /// <exception cref="Exceptions.OutgoingFiltersBlockedException">An outgoing filter returned <see cref="FilterAction.Stop"/>.</exception>
    /// <exception cref="Exceptions.RequestSendCancelledException">The outbound send pipeline was cancelled (e.g. transport failure) without the caller token firing.</exception>
    /// <exception cref="Exceptions.RequestTimeoutException">No reply arrived within <c>options.Timeout</c>.</exception>
    /// <exception cref="OperationCanceledException">The caller's <paramref name="cancellationToken"/> fired.</exception>
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
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><c>options.Timeout</c> is negative (other than <see cref="System.Threading.Timeout.Infinite"/>) or zero.</exception>
    /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
    /// <exception cref="Exceptions.OutgoingFiltersBlockedException">An outgoing filter returned <see cref="FilterAction.Stop"/>.</exception>
    /// <exception cref="Exceptions.RequestSendCancelledException">The outbound send pipeline was cancelled without the caller token firing.</exception>
    /// <exception cref="Exceptions.RequestTimeoutException">No reply arrived within <c>options.Timeout</c>, or fewer than <c>options.ExpectedReplyCount</c> replies arrived (when positive). Partials are exposed on <see cref="Exceptions.RequestTimeoutException.PartialReplies"/>.</exception>
    /// <exception cref="OperationCanceledException">The caller's <paramref name="cancellationToken"/> fired.</exception>
    Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(TRequest message, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where TRequest : Message where TReply : Message;

    /// <summary>
    /// Publishes a request and invokes a callback for each reply received.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Parameter order differs from <see cref="PublishAsync"/>/<see cref="SendAsync"/>
    /// (which put <c>options</c> second): <paramref name="onReply"/> is required and C#
    /// does not allow an optional parameter (<c>options</c>) to precede a required one,
    /// so the callback must come second. The alternative — making <c>options</c>
    /// required — would force every caller to pass <see cref="RequestOptions.Default"/>
    /// explicitly, which is worse ergonomics than the position asymmetry.
    /// </para>
    /// <para>
    /// <b>Callback contract.</b> An exception thrown from <paramref name="onReply"/> terminates
    /// the request — the awaited task faults with that exception, the request is closed, and
    /// every subsequent matching reply is silently dropped. Wrap the callback body in
    /// <c>try/catch</c> if log-and-continue per-reply semantics are wanted.
    /// </para>
    /// <para>
    /// <b>EndPoint.</b> <see cref="RequestOptions.EndPoint"/> must be <see langword="null"/> or
    /// empty for this method — request-publish is fanout-only. A non-empty <c>EndPoint</c>
    /// throws <see cref="ArgumentException"/>; use <see cref="SendRequestAsync"/> for
    /// single-destination requests.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> or <paramref name="onReply"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><c>options.EndPoint</c> is non-empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><c>options.Timeout</c> is negative (other than <see cref="System.Threading.Timeout.Infinite"/>) or zero.</exception>
    /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
    /// <exception cref="Exceptions.OutgoingFiltersBlockedException">An outgoing filter returned <see cref="FilterAction.Stop"/>.</exception>
    /// <exception cref="Exceptions.RequestTimeoutException">No replies arrived within <c>options.Timeout</c>, or fewer than <c>options.ExpectedReplyCount</c> replies arrived.</exception>
    /// <exception cref="OperationCanceledException">The caller's <paramref name="cancellationToken"/> fired, or an <paramref name="onReply"/> invocation threw and propagated through the awaited task.</exception>
    Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where TRequest : Message where TReply : Message;

    /// <summary>
    /// Routes a message through a series of destinations using a routing slip.
    /// </summary>
    /// <remarks>
    /// An outgoing filter returning <see cref="FilterAction.Stop"/> causes this call
    /// to return silently — the routing slip is dropped before transport publish.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> or <paramref name="destinations"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="destinations"/> is empty or contains an entry with a comma (the routing-slip separator) or that otherwise fails destination validation.</exception>
    /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
    Task RouteAsync<T>(T message, IReadOnlyList<string> destinations, CancellationToken cancellationToken = default) where T : Message;

    /// <summary>
    /// Creates a streaming connection for sending large messages in chunks.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="IMessageBusWriteStream"/> latches into a faulted state on the
    /// first transport failure (e.g. <c>PublishException</c> from a deleted destination
    /// queue); subsequent <c>WriteAsync</c> calls throw <see cref="InvalidOperationException"/>
    /// and cannot recover. Create a fresh stream after any write failure.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="endpoint"/> is <see langword="null"/> or whitespace.</exception>
    /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
    /// <exception cref="InvalidOperationException">No <see cref="IProducer"/> is registered in the bus's DI graph.</exception>
    IMessageBusWriteStream CreateStream<T>(string endpoint) where T : Message;

    /// <summary>
    /// Starts consuming messages from the configured queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Single-use lifecycle.</b> The bus is single-use with respect to its consuming state.
    /// Once <see cref="StopConsumingAsync"/> has been called (or <see cref="IAsyncDisposable.DisposeAsync"/>
    /// has run), the internal stopped flag is latched permanently and this method throws
    /// <see cref="InvalidOperationException"/> on any subsequent call. There is no reset path.
    /// </para>
    /// <para>
    /// To resume consumption after a stop, dispose the current bus instance and resolve (or
    /// construct) a fresh one. In a DI container, this typically means ending the DI lifetime
    /// scope that owns the bus singleton and starting a new one — <em>not</em> calling
    /// <c>StartConsumingAsync</c> again on the same instance.
    /// </para>
    /// <para>
    /// Also throws <see cref="InvalidOperationException"/> if the bus is already consuming
    /// (i.e. a concurrent or duplicate <c>StartConsumingAsync</c> call is in progress or has
    /// already completed).
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The bus is already consuming, or has previously been stopped.
    /// </exception>
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
    /// Gets whether the bus has been stopped or is being disposed. Distinct from
    /// <see cref="IsConsuming"/>: that getter also flips false during a transient broker
    /// disconnect (which the health check's recovery-grace window absorbs), whereas this
    /// flag flips true permanently once <see cref="StopConsumingAsync"/> or
    /// <see cref="IAsyncDisposable.DisposeAsync"/> has run, signalling that there is no
    /// recovery to wait for. <c>BusConsumingHealthCheck</c> uses it to bypass grace and
    /// report <c>Unhealthy</c> immediately on intentional shutdown. Default implementation
    /// returns <see langword="false"/>; framework-supplied buses override.
    /// </summary>
    bool IsStopped => false;

    /// <summary>
    /// Schedules a <see cref="TimeoutMessage"/> to be delivered to the current queue
    /// after the specified delay. The message's <c>CorrelationId</c> will equal
    /// <paramref name="correlationId"/>, which is the standard key for Process
    /// Manager correlation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="correlationId"/> MUST be the saga's own <c>data.CorrelationId</c>
    /// — the key the process manager finder will use to load the saga when the timeout
    /// fires. A common programmer mistake is to pass the incoming <c>message.CorrelationId</c>
    /// instead, which only matches the saga's own correlation id when the inbound message
    /// is the one that started the saga. Passing the wrong id silently inserts a stray
    /// timeout row whose dispatch will call <c>IProcessManagerFinder.FindData</c> against
    /// an id that no saga owns; the timeout is then dropped after retries with no recovery
    /// path. There is no runtime way for the bus to validate the supplied id corresponds
    /// to the active saga — handlers are responsible for passing the correct id.
    /// </para>
    /// <para>
    /// Two failure shapes for "this bus cannot schedule timeouts" coexist for compatibility:
    /// the default-interface-method on this property returns a faulted task with
    /// <see cref="NotSupportedException"/>; the first-party <c>Bus</c> implementation throws
    /// <see cref="InvalidOperationException"/> when no <c>ITimeoutStore</c> is registered.
    /// Callers that need to detect "not configured for timeouts" should catch both.
    /// </para>
    /// </remarks>
    /// <param name="correlationId">
    /// The saga's own correlation id (<c>data.CorrelationId</c>). Must not be <see cref="Guid.Empty"/>;
    /// see remarks for the message-vs-saga correlation pitfall.
    /// </param>
    /// <param name="delay">Time from now after which the timeout message is delivered. Must be strictly positive.</param>
    /// <param name="cancellationToken">Cancels the timeout-store insert; the scheduled delivery itself is not cancellable post-insert.</param>
    /// <exception cref="ArgumentException"><paramref name="correlationId"/> is <see cref="Guid.Empty"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="delay"/> is less than or equal to <see cref="TimeSpan.Zero"/>.</exception>
    /// <exception cref="InvalidOperationException">The bus has no <c>ITimeoutStore</c> registered (first-party Bus only — see remarks).</exception>
    /// <exception cref="NotSupportedException">The bus implementation does not support scheduling timeouts (default-interface-method path — see remarks).</exception>
    /// <exception cref="ObjectDisposedException">The bus has been disposed.</exception>
    Task RequestTimeoutAsync(Guid correlationId, TimeSpan delay, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("This IBus implementation does not support scheduling timeouts."));
}
