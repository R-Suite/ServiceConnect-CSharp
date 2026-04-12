using RabbitMQ.Client;

namespace ServiceConnect.Client.RabbitMQ;

public interface IServiceConnectConnection : IAsyncDisposable
{
    Task ConnectAsync();
    Task<IChannel> CreateChannelAsync();
    bool IsConnected();
}
