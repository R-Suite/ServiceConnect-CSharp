using System.Diagnostics.CodeAnalysis;

namespace ServiceConnect.Interfaces.Exceptions;

/// <summary>
/// Represents a request/reply operation that exceeded its timeout.
/// </summary>
/// <remarks>
/// When raised by <c>SendRequestMultiAsync</c> with a positive
/// <c>RequestOptions.ExpectedReplyCount</c>, <see cref="PartialReplies"/> exposes the
/// replies received before the timeout fired. When raised by other paths
/// (<c>SendRequestAsync</c>, <c>PublishRequestAsync</c>), <see cref="PartialReplies"/>
/// is an empty list.
/// </remarks>
// IDE0290 (prefer primary constructor): two ctor overloads are required — the 2-arg
// shape is the binary-compatible default for callers that don't carry partials, and the
// 3-arg shape carries SendRequestMultiAsync's partial replies. A primary ctor cannot
// express the chained-this default while keeping the 2-arg shape callable.
[SuppressMessage("Style", "IDE0290:Use primary constructor", Justification = "Two ctor overloads required for binary-compat 2-arg shape and partial-replies 3-arg shape.")]
public sealed class RequestTimeoutException : ServiceConnectException
{
    /// <summary>
    /// Initialises the exception with no partial replies. Used by <c>SendRequestAsync</c>
    /// and <c>PublishRequestAsync</c>, neither of which carries a buffered reply set.
    /// </summary>
    /// <param name="correlationId">The correlation id of the timed-out request.</param>
    /// <param name="elapsed">The time spent waiting for replies.</param>
    public RequestTimeoutException(Guid correlationId, TimeSpan elapsed)
        : this(correlationId, elapsed, partialReplies: [])
    {
    }

    /// <summary>
    /// Initialises the exception with the partial replies the caller's request received
    /// before the timeout fired. Used by <c>SendRequestMultiAsync</c> on under-delivery.
    /// </summary>
    /// <param name="correlationId">The correlation id of the timed-out request.</param>
    /// <param name="elapsed">The time spent waiting for replies.</param>
    /// <param name="partialReplies">
    /// The replies that arrived before the timeout fired. May be empty. <see langword="null"/>
    /// is treated as an empty list rather than throwing — the exception is a pure data
    /// carrier and the caller is already on a failure path.
    /// </param>
    public RequestTimeoutException(Guid correlationId, TimeSpan elapsed, IReadOnlyList<object> partialReplies)
        : base(System.FormattableString.Invariant(
            $"Request {correlationId} timed out after {elapsed.TotalMilliseconds}ms"))
    {
        CorrelationId = correlationId;
        Elapsed = elapsed;
        PartialReplies = partialReplies ?? [];
    }

    /// <summary>
    /// Gets the correlation id of the timed-out request.
    /// </summary>
    public Guid CorrelationId { get; }

    /// <summary>
    /// Gets the elapsed waiting time.
    /// </summary>
    public TimeSpan Elapsed { get; }

    /// <summary>
    /// Gets the replies received before the timeout fired. Empty for paths that don't
    /// surface partials (e.g., single-reply <c>SendRequestAsync</c>,
    /// <c>PublishRequestAsync</c>). Populated by <c>SendRequestMultiAsync</c> when the
    /// caller specified a positive <c>ExpectedReplyCount</c> and fewer replies arrived
    /// before the timeout.
    /// </summary>
    public IReadOnlyList<object> PartialReplies { get; }
}
