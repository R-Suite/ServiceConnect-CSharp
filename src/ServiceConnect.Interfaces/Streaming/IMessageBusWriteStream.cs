namespace ServiceConnect.Interfaces;

public interface IMessageBusWriteStream : IAsyncDisposable
{
    Task WriteAsync(byte[] buffer, int offset, int count);
    Task CloseAsync();
}
