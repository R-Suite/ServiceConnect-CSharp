using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class ReplyProcessor(IRequestReplyManager replyManager) : IMessageProcessor
{
    // Cache the two result tasks so enum-boxing allocation doesn't happen per message (P-44).
    private static readonly Task<ProcessResult> NotHandledTask = Task.FromResult(ProcessResult.NotHandled);
    private static readonly Task<ProcessResult> HandledTask = Task.FromResult(ProcessResult.Handled);

    public bool RunBeforeDeserialization => true;

    public Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!headers.TryGetValue(HeaderKeys.ResponseMessageId, out var responseMessageIdRaw))
            return NotHandledTask;

        var responseMessageId = HeaderDecoder.Decode(responseMessageIdRaw);

        if (string.IsNullOrEmpty(responseMessageId))
            return NotHandledTask;

        replyManager.ProcessReply(responseMessageId, messageBytes, messageType);
        return HandledTask;
    }
}
