namespace ServiceConnect.Interfaces;

/// <summary>
/// Reassembles a streamed sequence of message packets into a readable payload.
/// </summary>
/// <remarks>
/// <para>
/// <b>SequenceId uniqueness contract.</b> The framework admits stream packets by
/// <see cref="SequenceId"/>. Producers MUST generate a fresh <see cref="Guid"/> for
/// every call to <c>IBus.CreateStream&lt;T&gt;</c>; the framework's
/// <c>IMessageBusWriteStream</c> implementation does so internally. Two producers
/// that explicitly construct the same <see cref="SequenceId"/> will write into the
/// same in-flight stream — the receiver cannot distinguish them, and packets from
/// the second producer will collide at the framework-enforced contiguous
/// <c>PacketNumber</c> invariant, faulting both senders' streams.
/// </para>
/// <para>
/// The collision is bounded by per-stream caps (active-stream count, total stream
/// size, packet-number ceiling) so a misbehaving or hostile producer cannot
/// arbitrarily corrupt unrelated streams, but two cooperating producers must not
/// share a SequenceId.
/// </para>
/// </remarks>
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
    /// <exception cref="InvalidOperationException">
    /// Thrown when the stream is not yet complete, or when a packet is missing during assembly.
    /// A missing-packet condition is unrecoverable; treat the stream as corrupt and discard it.
    /// </exception>
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
    /// <exception cref="InvalidOperationException">
    /// Thrown when the stream is not yet complete, or when a packet is missing during assembly.
    /// A missing-packet condition is unrecoverable; treat the stream as corrupt and discard it.
    /// </exception>
    System.Buffers.ReadOnlySequence<byte> ReadSequence()
        => new(Read());
}
