namespace ServiceConnect.Interfaces;

public interface IProducer : IAsyncDisposable, IDisposable
{
    Task PublishAsync(Type type, byte[] message, Dictionary<string, string>? headers = null);
    Task SendAsync(Type type, byte[] message, Dictionary<string, string>? headers = null);
    Task SendAsync(string endPoint, Type type, byte[] message, Dictionary<string, string>? headers = null);
    Task SendBytesAsync(string endPoint, byte[] packet, Dictionary<string, string>? headers = null);
    long MaximumMessageSize { get; }
    Task DisconnectAsync();
}
