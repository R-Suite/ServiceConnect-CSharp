using RabbitMQ.Client;

namespace ServiceConnect.Client.RabbitMQ;

public interface IServiceConnectConnection
{
    Task ConnectAsync();
    Task<IChannel> CreateChannelAsync();
    void Dispose();
    bool IsConnected();
}
