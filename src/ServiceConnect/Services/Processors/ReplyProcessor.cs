using System.Text;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public class ReplyProcessor : IMessageProcessor
{
    private readonly IRequestReplyManager _replyManager;

    public ReplyProcessor(IRequestReplyManager replyManager)
    {
        _replyManager = replyManager;
    }

    public bool RunBeforeDeserialization => true;

    public Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope)
    {
        if (!headers.TryGetValue(HeaderKeys.ResponseMessageId, out var responseMessageIdRaw))
            return Task.FromResult(ProcessResult.NotHandled);

        var responseMessageId = responseMessageIdRaw is byte[] bytes
            ? Encoding.UTF8.GetString(bytes)
            : responseMessageIdRaw?.ToString();

        if (string.IsNullOrEmpty(responseMessageId))
            return Task.FromResult(ProcessResult.NotHandled);

        _replyManager.ProcessReply(responseMessageId, messageBytes, messageType);
        return Task.FromResult(ProcessResult.Handled);
    }
}
