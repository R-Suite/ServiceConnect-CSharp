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
    /// The maximum aggregation window. Returning <see langword="default"/> disables the timeout-based flush.
    /// </returns>
    public virtual TimeSpan Timeout()
    {
        return default;
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
    /// <param name="messages">The messages collected for the batch.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>A task that completes when the batch has been processed.</returns>
    public abstract Task ExecuteAsync(IList<T> messages, CancellationToken cancellationToken = default);
}
