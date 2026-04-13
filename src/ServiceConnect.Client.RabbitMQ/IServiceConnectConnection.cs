using RabbitMQ.Client;

namespace ServiceConnect.Client.RabbitMQ;

public interface IServiceConnectConnection : IAsyncDisposable
{
    Task<IChannel> CreateChannelAsync();
    bool IsConnected();
}
