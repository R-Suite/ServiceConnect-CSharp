using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Interfaces;

public interface IBus : IDisposable
{
    Task PublishAsync<T>(T message, PublishOptions? options = null) where T : Message;
    Task SendAsync<T>(T message, SendOptions? options = null) where T : Message;
    Task<TReply> SendRequestAsync<T, TReply>(T message, RequestOptions? options = null)
        where T : Message where TReply : Message;
    Task<IList<TReply>> SendRequestMultiAsync<T, TReply>(T message, RequestOptions? options = null)
        where T : Message where TReply : Message;
    Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null)
        where TRequest : Message where TReply : Message;
    Task RouteAsync<T>(T message, IList<string> destinations) where T : Message;
    IMessageBusWriteStream CreateStream<T>(string endpoint, T message) where T : Message;
    Task StartConsumingAsync();
    void StopConsuming();
    bool IsConnected { get; }
}
