namespace ServiceConnect.Interfaces;

public delegate Task SendMessageDelegate(
    Type typeObject, byte[] messageBytes,
    Dictionary<string, string> headers, string? endPoint,
    CancellationToken cancellationToken);

public interface ISendMessageMiddleware
{
    Task Process(Type typeObject, byte[] messageBytes,
        Dictionary<string, string> headers, string? endPoint,
        SendMessageDelegate next,
        CancellationToken cancellationToken);
}
