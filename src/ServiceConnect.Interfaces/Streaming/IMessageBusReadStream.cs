namespace ServiceConnect.Interfaces;

/// <summary>
/// Reassembles a streamed sequence of message packets into a readable payload.
/// </summary>
public interface IMessageBusReadStream
{
    /// <summary>
    /// Writes a packet into the stream buffer.
    /// </summary>
    /// <param name="data">The packet payload. The implementation copies the bytes for retention;
    /// the caller's buffer can be reused after the call returns.</param>
    /// <param name="packetNumber">The zero-based packet number.</param>
    void Write(ReadOnlyMemory<byte> data, long packetNumber);

    /// <summary>
    /// Reads the assembled payload as a single byte array.
    /// </summary>
    /// <returns>The assembled payload.</returns>
    byte[] Read();

    /// <summary>
    /// Determines whether all expected packets have been received.
    /// </summary>
    /// <returns><see langword="true"/> when the stream is complete; otherwise <see langword="false"/>.</returns>
    bool IsComplete();

    /// <summary>
    /// Sets the final packet number expected for the stream.
    /// </summary>
    /// <param name="lastPacketNumber">The final packet number.</param>
    void SetLastPacketNumber(long lastPacketNumber);

    /// <summary>
    /// Gets the final packet number expected for the stream.
    /// </summary>
    long LastPacketNumber { get; }

    /// <summary>
    /// Gets the stream sequence identifier.
    /// </summary>
    string SequenceId { get; }

    /// <summary>
    /// Reads the assembled payload as a <see cref="System.Buffers.ReadOnlySequence{T}"/>.
    /// </summary>
    /// <returns>The assembled payload sequence.</returns>
    System.Buffers.ReadOnlySequence<byte> ReadSequence()
        => new(Read());
}
