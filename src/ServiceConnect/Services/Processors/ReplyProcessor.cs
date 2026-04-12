using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public sealed class ReplyProcessor(IRequestReplyManager replyManager) : IMessageProcessor
{
    public bool RunBeforeDeserialization => true;

    public Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope)
    {
        if (!headers.TryGetValue(HeaderKeys.ResponseMessageId, out var responseMessageIdRaw))
            return Task.FromResult(ProcessResult.NotHandled);

        var responseMessageId = HeaderDecoder.Decode(responseMessageIdRaw);

        if (string.IsNullOrEmpty(responseMessageId))
            return Task.FromResult(ProcessResult.NotHandled);

        replyManager.ProcessReply(responseMessageId, messageBytes, messageType);
        return Task.FromResult(ProcessResult.Handled);
    }
}
