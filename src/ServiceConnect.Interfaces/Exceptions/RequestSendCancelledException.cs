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
public sealed class RequestSendCancelledException(Guid messageId, string message)
    : OperationCanceledException(message)
{
    /// <summary>
    /// Gets the request id of the send that was cancelled.
    /// </summary>
    public Guid MessageId { get; } = messageId;
}
