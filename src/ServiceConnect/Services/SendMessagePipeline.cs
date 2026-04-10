using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

/// <summary>
/// Default implementation of ISendMessagePipeline that delegates directly to IProducer.
/// </summary>
public class SendMessagePipeline : ISendMessagePipeline
{
    private readonly IProducer _producer;

    public SendMessagePipeline(IProducer producer)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
    }

    public Task ExecutePublishMessagePipelineAsync(Type typeObject, byte[] messageBytes, Dictionary<string, string> headers = null!, string endPoint = null!)
    {
        return _producer.PublishAsync(typeObject, messageBytes, headers);
    }

    public Task ExecuteSendMessagePipelineAsync(Type typeObject, byte[] messageBytes, Dictionary<string, string> headers = null!, string endPoint = null!)
    {
        if (!string.IsNullOrEmpty(endPoint))
            return _producer.SendAsync(endPoint, typeObject, messageBytes, headers);

        return _producer.SendAsync(typeObject, messageBytes, headers);
    }

    public void Dispose()
    {
        _producer.Dispose();
    }
}
