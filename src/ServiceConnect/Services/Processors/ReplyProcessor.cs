using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class ReplyProcessor(IReplyStatusRequestReplyManager? replyManager) : IMessageProcessor
{
    // Cache the two result tasks so enum-boxing allocation doesn't happen per message.
    private static readonly Task<ProcessResult> NotHandledTask = Task.FromResult(ProcessResult.NotHandled);
    private static readonly Task<ProcessResult> HandledTask = Task.FromResult(ProcessResult.Handled);

    public bool RunBeforeDeserialization => true;

    public Task<ProcessResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!headers.TryGetValue(HeaderKeys.ResponseMessageId, out var responseMessageIdRaw))
        {
            return NotHandledTask;
        }

        var responseMessageId = HeaderDecoder.Decode(responseMessageIdRaw);

        if (string.IsNullOrEmpty(responseMessageId))
        {
            return NotHandledTask;
        }

        if (replyManager == null)
        {
            return NotHandledTask;
        }

        if (replyManager.TryProcessReply(responseMessageId, messageBytes, messageType))
        {
            return HandledTask;
        }

        return NotHandledTask;
    }
}
