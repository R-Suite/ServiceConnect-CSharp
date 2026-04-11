namespace ServiceConnect.Interfaces;

public delegate Task<ConsumeEventResult> MessageProcessingDelegate(
    byte[] messageBytes, Type messageType, object message,
    IDictionary<string, object> headers, Envelope envelope);

public interface IMessageProcessingMiddleware
{
    MessageProcessingDelegate Next { get; set; }

    Task<ConsumeEventResult> Process(
        byte[] messageBytes, Type messageType, object message,
        IDictionary<string, object> headers, Envelope envelope);
}
