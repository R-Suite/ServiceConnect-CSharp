namespace ServiceConnect.Interfaces;

public enum ProcessResult { Handled, NotHandled }

public interface IMessageProcessor
{
    /// <summary>
    /// When true, this processor can run before the message body is deserialized
    /// (the message parameter will be null). Pre-deserialization processors are
    /// invoked before filters and before the serializer is called.
    /// </summary>
    bool RunBeforeDeserialization => false;

    Task<ProcessResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes,
        Type messageType,
        object? message,
        IDictionary<string, object> headers,
        Envelope envelope,
        CancellationToken cancellationToken = default);
}
