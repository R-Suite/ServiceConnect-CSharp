namespace ServiceConnect.Services;

internal interface IReplyStatusRequestReplyManager
{
    bool TryProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type);
    bool IsTrackedRequest(string messageId);
}
