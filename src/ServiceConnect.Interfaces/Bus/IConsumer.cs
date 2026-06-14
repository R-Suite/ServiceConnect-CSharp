namespace ServiceConnect.Interfaces;

/// <summary>
/// Consumes messages from the message broker.
/// </summary>
public interface IConsumer : IAsyncDisposable
{
    /// <summary>
    /// Gets whether the consumer is currently connected to the broker.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Gets whether the broker has cancelled this consumer (e.g. queue deleted, queue policy
    /// expired, mirror promoted). When true the consumer is no longer receiving deliveries
    /// from the broker; <see cref="IBus.IsConsuming"/> returns false to signal the unhealthy state.
    /// </summary>
    bool IsCancelledByBroker { get; }

    /// <summary>
    /// Starts consuming messages from the specified queue for the given message types.
    /// </summary>
    Task StartConsumingAsync(string queueName, IReadOnlyList<string> messageTypes, ConsumerEventHandler eventHandler, CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues a graceful stop: instructs the broker to stop delivering messages to
    /// this consumer and drains any in-flight handler invocations. Does NOT tear
    /// down the underlying channel/connection — that happens on
    /// <see cref="IAsyncDisposable.DisposeAsync"/>. Idempotent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default-interface-method is a no-op so existing third-party
    /// <see cref="IConsumer"/> implementations remain source-compatible. Custom
    /// transports that want graceful shutdown semantics should override this — without
    /// an override, <c>Bus.StopConsumingAsync</c> only flips the consuming flag and
    /// the broker keeps delivering until DI disposal.
    /// </para>
    /// <para>
    /// <b>Handler cooperation.</b> Drain semantics depend on every in-flight handler
    /// observing the <see cref="CancellationToken"/> threaded through dispatch. A
    /// handler that performs synchronous I/O or ignores its <c>CancellationToken</c>
    /// will block the drain for the full duration of that work. Implementations
    /// should bound their own drain wait by the host's graceful-shutdown grace
    /// window rather than waiting indefinitely.
    /// </para>
    /// </remarks>
    Task StopConsumingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Gets whether the consumer has been stopped or is being disposed. Distinct from
    /// <see cref="IsConnected"/>: that getter also flips false during a transient broker
    /// disconnect, whereas this flag flips true permanently once
    /// <see cref="StopConsumingAsync"/> or <see cref="IAsyncDisposable.DisposeAsync"/>
    /// has run, signalling that there is no recovery to wait for.
    /// <c>ConsumerConnectionHealthCheck</c> uses it to bypass the recovery-grace window
    /// on intentional shutdown. Default implementation returns <see langword="false"/>.
    /// </summary>
    bool IsStopped => false;
}
