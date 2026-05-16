namespace ServiceConnect.Interfaces;

/// <summary>
/// Defines an aggregator that batches related messages before handling them.
/// </summary>
/// <typeparam name="T">The message type accepted by the aggregator.</typeparam>
public abstract class Aggregator<T> where T : Message
{
    /// <summary>
    /// Gets the maximum amount of time to wait before dispatching the current batch.
    /// </summary>
    /// <returns>
    /// The maximum aggregation window. Returning <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// (the default) disables the timeout-based flush.
    /// </returns>
    public virtual TimeSpan Timeout()
    {
        // Timeout.InfiniteTimeSpan (-1ms) is the BCL convention for "no timeout". Returning
        // TimeSpan.Zero would let the dispatcher mistake "fire immediately and once" for
        // "disabled" — the InfiniteTimeSpan sentinel is unambiguous.
        return System.Threading.Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// Gets the maximum number of messages to buffer before dispatching the batch.
    /// </summary>
    /// <returns>
    /// The batch size limit. Returning <c>0</c> means no size-based flush is enforced.
    /// </returns>
    public virtual int BatchSize()
    {
        return 0;
    }

    /// <summary>
    /// Processes a completed batch of aggregated messages.
    /// </summary>
    /// <param name="messages">The messages collected for the batch. Read-only — handlers must not
    /// mutate the snapshot they were handed; the persistor owns the underlying buffer's lifetime.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>A task that completes when the batch has been processed.</returns>
    public abstract Task ExecuteAsync(IReadOnlyList<T> messages, CancellationToken cancellationToken = default);
}
