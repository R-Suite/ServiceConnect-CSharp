namespace ServiceConnect.Interfaces;

/// <summary>
/// Splits a payload into transport packets and writes them to the message bus.
/// </summary>
public interface IMessageBusWriteStream : IAsyncDisposable
{
    /// <summary>
    /// Writes a slice of the source buffer to the stream.
    /// </summary>
    /// <param name="buffer">The source buffer.</param>
    /// <param name="offset">The starting offset within the buffer.</param>
    /// <param name="count">The number of bytes to write.</param>
    /// <param name="cancellationToken">Token that cancels the write before it is dispatched to the producer.</param>
    Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flushes any remaining data and marks the stream as complete.
    /// </summary>
    /// <param name="cancellationToken">Token that aborts the in-flight drain wait and the close send.</param>
    Task CloseAsync(CancellationToken cancellationToken = default);
}
