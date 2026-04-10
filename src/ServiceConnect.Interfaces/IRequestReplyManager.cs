namespace ServiceConnect.Interfaces;

public interface IRequestReplyManager
{
    Task<TReply> SendRequestAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        Action<Type, byte[], Dictionary<string, string>, string?> sendAction,
        RequestOptions options)
        where TRequest : Message
        where TReply : Message;

    Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(
        byte[] messageBytes,
        Dictionary<string, string> headers,
        Action<Type, byte[], Dictionary<string, string>, string?> sendAction,
        RequestOptions options)
        where TRequest : Message
        where TReply : Message;

    void ProcessReply(string messageId, string messageJson, Type type);
}
