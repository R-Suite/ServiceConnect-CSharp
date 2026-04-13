namespace ServiceConnect.Interfaces;

public interface ISendMessagePipeline : IDisposable
{
    Task ExecutePublishMessagePipelineAsync(Type typeObject, byte[] messageBytes, Dictionary<string, string>? headers = null, string? endPoint = null, CancellationToken cancellationToken = default);
    Task ExecuteSendMessagePipelineAsync(Type typeObject, byte[] messageBytes, Dictionary<string, string>? headers = null, string? endPoint = null, CancellationToken cancellationToken = default);
}