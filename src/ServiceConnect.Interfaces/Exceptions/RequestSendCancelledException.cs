namespace ServiceConnect.Interfaces.Exceptions;

/// <summary>
/// Thrown by <see cref="IRequestReplyManager.SendRequestAsync{TRequest, TReply}"/>,
/// <see cref="IRequestReplyManager.SendRequestMultiAsync{TRequest, TReply}"/>, and
/// <see cref="IRequestReplyManager.PublishRequestAsync{TRequest, TReply}"/> when the
/// outbound send was cancelled before delivery — distinct from the caller's own
/// cancellation token firing (which surfaces as a plain <see cref="OperationCanceledException"/>)
/// and from a request timeout (which surfaces as <see cref="RequestTimeoutException"/>).
/// Inherits from <see cref="OperationCanceledException"/> so existing
/// <c>catch (OperationCanceledException)</c> handlers continue to catch it; callers can
/// catch this type specifically to react to send-layer failures.
/// </summary>
public sealed class RequestSendCancelledException : OperationCanceledException
{
    /// <summary>
    /// Initializes a new instance carrying the request id and the cancellation token that fired.
    /// </summary>
    public RequestSendCancelledException(Guid messageId, string message, CancellationToken cancellationToken = default)
        : base(message, cancellationToken)
    {
        MessageId = messageId;
    }

    /// <summary>
    /// Initializes a new instance carrying the request id, the underlying transport / pipeline
    /// failure that caused the cancellation, and the cancellation token that fired. Use this
    /// overload when the send-layer surfaces a real root cause (broker unreachable, channel
    /// closed, filter pipeline rejected) so callers see the diagnostic chain rather than just
    /// the cancellation symptom.
    /// </summary>
    public RequestSendCancelledException(Guid messageId, string message, Exception? innerException, CancellationToken cancellationToken = default)
        : base(message, innerException, cancellationToken)
    {
        MessageId = messageId;
    }

    /// <summary>
    /// Gets the request id of the send that was cancelled.
    /// </summary>
    public Guid MessageId { get; }
}
