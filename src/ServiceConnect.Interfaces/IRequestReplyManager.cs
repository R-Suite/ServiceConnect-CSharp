using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Interfaces;

public interface IRequestReplyManager
{
    Task<TReply> SendRequestAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        Func<Type, byte[], Dictionary<string, string>, string?, CancellationToken, Task> sendAction,
        RequestOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message;

    Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        Func<Type, byte[], Dictionary<string, string>, string?, CancellationToken, Task> sendAction,
        RequestOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message;

    void ProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type);
}
