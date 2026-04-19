namespace ServiceConnect.Interfaces;

public delegate Task<ConsumeEventResult> MessageProcessingDelegate(
    ReadOnlyMemory<byte> messageBytes, Type messageType, object message,
    IDictionary<string, object> headers, Envelope envelope,
    CancellationToken cancellationToken);

public interface IMessageProcessingMiddleware
{
    Task<ConsumeEventResult> Process(
        ReadOnlyMemory<byte> messageBytes, Type messageType, object message,
        IDictionary<string, object> headers, Envelope envelope,
        MessageProcessingDelegate next,
        CancellationToken cancellationToken);
}
