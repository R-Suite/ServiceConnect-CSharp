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
    /// Starts consuming messages from the specified queue for the given message types.
    /// </summary>
    Task StartConsumingAsync(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler, CancellationToken cancellationToken = default);
}
