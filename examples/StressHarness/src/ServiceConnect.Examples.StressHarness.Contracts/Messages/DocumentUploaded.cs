using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// Control message carried by the stress harness's streaming pattern. The driver
/// JSON-serialises one of these and sends the resulting bytes in chunks through
/// <see cref="ServiceConnect.Interfaces.IBus.CreateStream{T}"/>; the framework
/// reassembles the bytes and deserialises them back into a <see cref="DocumentUploaded"/>
/// on the receiver side, also handing the raw byte buffer to the
/// <see cref="ServiceConnect.Interfaces.IStreamHandler{TMessage}"/> via
/// <see cref="ServiceConnect.Interfaces.IMessageBusReadStream"/> for integrity checks.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Message.CorrelationId"/> carries the flow id because the framework's
/// stream API exposes no caller-visible header pathway —
/// <see cref="ServiceConnect.Interfaces.IMessageBusWriteStream.WriteAsync"/> only
/// accepts a byte buffer and a cancellation token, and the producer stamps the
/// reserved headers (SequenceId / PacketNumber / FullTypeName) from internal state
/// rather than from caller-supplied options. The aggregator driver uses the same
/// body-only correlation strategy for the same reason.
/// </para>
/// <para>
/// <see cref="Payload"/> carries deterministic bytes the driver fills to inflate
/// the serialised message past a single transport packet, so the chunked write
/// path is exercised end-to-end rather than fitting the whole payload in one
/// packet. The hash of the serialised JSON is what the driver and the receiver
/// compare — both ends compute SHA-256 of the wire bytes.
/// </para>
/// </remarks>
public sealed class DocumentUploaded(Guid correlationId) : Message(correlationId)
{
    /// <summary>File name echoed in the receiver's observation log.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>
    /// Deterministic payload bytes filled by the driver to inflate the serialised
    /// message length past a single transport packet. The receiver does not inspect
    /// the contents directly — the integrity assertion runs over the full
    /// stream-reassembled byte buffer, of which this property is the majority.
    /// </summary>
    public byte[] Payload { get; init; } = [];
}
