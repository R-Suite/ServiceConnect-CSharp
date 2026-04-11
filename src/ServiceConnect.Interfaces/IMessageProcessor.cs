namespace ServiceConnect.Interfaces;

public enum ProcessResult { Handled, NotHandled }

public interface IMessageProcessor
{
    Task<ProcessResult> ProcessAsync(
        byte[] messageBytes,
        Type messageType,
        object? message,
        IDictionary<string, object> headers,
        Envelope envelope);
}
