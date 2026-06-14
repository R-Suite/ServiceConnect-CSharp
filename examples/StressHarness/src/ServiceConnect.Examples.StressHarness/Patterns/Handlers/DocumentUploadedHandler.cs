using System.Security.Cryptography;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns.Handlers;

/// <summary>
/// Stream handler for <see cref="DocumentUploaded"/>. Reads the reassembled byte
/// buffer from the framework's <see cref="IMessageBusReadStream"/>, computes the
/// SHA-256 digest, and records a <see cref="StreamObservation"/> the driver can
/// compare against the sent-side digest.
/// </summary>
/// <remarks>
/// <para>
/// The framework's <c>StreamProcessor</c> dispatches this handler once the close
/// packet has arrived and every data packet's bytes are present in the read
/// stream's buffer. <see cref="IMessageBusReadStream.Read"/> assembles the data
/// packets (the close packet's payload is empty by design — see
/// <c>MessageBusWriteStream.CloseAsync</c>) and returns the original byte
/// sequence the driver wrote.
/// </para>
/// <para>
/// Flow correlation rides on <see cref="Message.CorrelationId"/> because
/// <see cref="IBus.CreateStream{T}"/> exposes no caller-header pathway — the
/// producer stamps SequenceId / PacketNumber / FullTypeName from internal state
/// only. The aggregator driver uses the same body-only correlation strategy for
/// the same reason (its <c>ExecuteAsync</c> has no consume context).
/// </para>
/// </remarks>
public sealed class DocumentUploadedHandler(string busTag, FlowAccounting accounting, StreamObservations observations)
    : IStreamHandler<DocumentUploaded>
{
    public Task ExecuteAsync(DocumentUploaded message, IMessageBusReadStream stream, CancellationToken cancellationToken = default)
    {
        var bytes = stream.Read();
        var digest = SHA256.HashData(bytes);
        var hex = Convert.ToHexStringLower(digest);

        accounting.RecordHandled(message.CorrelationId);
        observations.Record(new StreamObservation(
            FlowId: message.CorrelationId,
            BusTag: busTag,
            Bytes: bytes.Length,
            Sha256: hex));
        return Task.CompletedTask;
    }
}
