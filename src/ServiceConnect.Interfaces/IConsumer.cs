namespace ServiceConnect.Interfaces;

public interface IConsumer : IAsyncDisposable, IDisposable
{
    bool IsConnected { get; }
    Task StartConsumingAsync(string queueName, IList<string> messageTypes, ConsumerEventHandler eventHandler);
}
