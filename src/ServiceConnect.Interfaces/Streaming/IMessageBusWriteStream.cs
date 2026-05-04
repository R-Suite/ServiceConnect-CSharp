namespace ServiceConnect.Interfaces;

/// <summary>
/// Splits a payload into transport packets and writes them to the message bus.
/// </summary>
public interface IMessageBusWriteStream : IAsyncDisposable
{
    /// <summary>
    /// Writes the supplied buffer to the stream as a single transport packet. The caller
    /// is responsible for chunking large payloads into multiple <c>WriteAsync</c> calls
    /// when packet sizes need to stay below a transport limit.
    /// </summary>
    /// <param name="buffer">The bytes to write. The buffer is read once and the underlying
    /// memory is not retained past the call completion; the caller can reuse the buffer.</param>
    /// <param name="cancellationToken">Token that cancels the write before it is dispatched
    /// to the producer.</param>
    Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flushes any remaining data and marks the stream as complete.
    /// </summary>
    /// <param name="cancellationToken">Token that aborts the in-flight drain wait and the close send.</param>
    Task CloseAsync(CancellationToken cancellationToken = default);
}
