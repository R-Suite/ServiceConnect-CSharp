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
    /// <remarks>
    /// <para>
    /// <b>Idempotency invariant.</b> <see cref="ExecuteAsync"/> MUST be safe to invoke more than
    /// once with the same logical batch. ServiceConnect delivers at-least-once: after the handler
    /// returns, the framework calls <c>RemoveSnapshotAsync</c> on the aggregator persistor to
    /// drop the dispatched records. A transient persistor failure between the handler's return and
    /// a successful remove leaves the records under their lease; when the lease expires the same
    /// batch is re-claimed and re-dispatched. Lease expiry under a slow handler produces the same
    /// replay. Side effects with external observability — outbound bus sends, HTTP calls, DB writes
    /// outside the aggregator's snapshot, file I/O — must therefore be guarded by an idempotency
    /// check (e.g., a deterministic key on the outbound message, an upsert with a deterministic
    /// key, a state flag persisted alongside the aggregator's own records). A handler that
    /// unconditionally <c>SendAsync</c>s an outbound command on every batch will double-send on
    /// replay; that is the framework's contract, not a bug.
    /// </para>
    /// <para>
    /// Cancellation behaviour: when <paramref name="cancellationToken"/> fires (host shutdown,
    /// dispatch-budget exhausted), an <see cref="OperationCanceledException"/> propagated out of
    /// the handler short-circuits the persistor remove and triggers a lease release. The same
    /// batch is then redelivered on the next eligible flush; idempotency rules above apply.
    /// </para>
    /// </remarks>
    public abstract Task ExecuteAsync(IReadOnlyList<T> messages, CancellationToken cancellationToken = default);
}
