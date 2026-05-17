namespace ServiceConnect.Interfaces;

/// <summary>
/// Defines an aggregator that batches related messages before handling them.
/// </summary>
/// <typeparam name="T">The message type accepted by the aggregator.</typeparam>
/// <remarks>
/// Every concrete subclass must declare its flush policy by overriding both
/// <see cref="BatchSize"/> and <see cref="Timeout"/>. The framework requires
/// both a size-based and a time-based flush path to guarantee buffered messages
/// always have a route to dispatch; the registry rejects subclasses whose
/// <see cref="BatchSize"/> is zero/negative or whose <see cref="Timeout"/> is
/// zero/<see cref="System.Threading.Timeout.InfiniteTimeSpan"/> with a startup
/// <see cref="InvalidOperationException"/>.
/// </remarks>
public abstract class Aggregator<T> where T : Message
{
    /// <summary>
    /// Gets the maximum amount of time to wait before dispatching the current batch.
    /// </summary>
    /// <returns>
    /// A positive <see cref="TimeSpan"/>. <see cref="TimeSpan.Zero"/> and
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> are rejected by the
    /// registry at startup — every aggregator must have a finite time-based flush path.
    /// </returns>
    public abstract TimeSpan Timeout();

    /// <summary>
    /// Gets the maximum number of messages to buffer before dispatching the batch.
    /// </summary>
    /// <returns>A positive integer. Zero and negative values are rejected by the registry at startup.</returns>
    public abstract int BatchSize();

    /// <summary>
    /// Processes a completed batch of aggregated messages.
    /// </summary>
    /// <param name="messages">The messages collected for the batch. Read-only — handlers must not
    /// mutate the snapshot they were handed; the persistor owns the underlying buffer's lifetime.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>A task that completes when the batch has been processed.</returns>
    public abstract Task ExecuteAsync(IReadOnlyList<T> messages, CancellationToken cancellationToken = default);
}
