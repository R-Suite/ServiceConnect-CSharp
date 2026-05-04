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
}
