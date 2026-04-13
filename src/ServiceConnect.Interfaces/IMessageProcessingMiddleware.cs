namespace ServiceConnect.Interfaces;

public delegate Task<ConsumeEventResult> MessageProcessingDelegate(
    byte[] messageBytes, Type messageType, object message,
    IDictionary<string, object> headers, Envelope envelope,
    CancellationToken cancellationToken);

public interface IMessageProcessingMiddleware
{
    Task<ConsumeEventResult> Process(
        byte[] messageBytes, Type messageType, object message,
        IDictionary<string, object> headers, Envelope envelope,
        MessageProcessingDelegate next,
        CancellationToken cancellationToken);
}
