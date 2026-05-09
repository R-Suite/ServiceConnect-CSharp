using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;
using Xunit;

namespace ServiceConnect.UnitTests.Processors;

[Collection(SerialConcurrencyCollection.Name)]
public class StreamProcessorEvictionRaceTests
{
    private static StreamProcessor BuildProcessor(TimeProvider? timeProvider = null)
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var accessor = new ConsumeScopeAccessor();
        accessor.Push(provider);
        return new StreamProcessor(
            accessor,
            NullLogger<StreamProcessor>.Instance,
            new MessageTypeRegistry(),
            new StreamHandlerRegistry([], NullLogger<StreamHandlerRegistry>.Instance),
            Mock.Of<IMessageSerializer>(),
            timeProvider ?? TimeProvider.System);
    }

    [Fact]
    public async Task ProcessAsync_EntryEvictedBeforeTouch_DropsPacketWithoutWriting()
    {
        // Pre-fix: Stream.Write committed bytes BEFORE the touch CAS. If eviction
        // raced between Write and the CAS, the CAS loop returned HandledTask but the
        // bytes had already landed in the now-orphaned MessageBusReadStream.
        // Post-fix: touch CAS runs first; if the entry has been evicted we return
        // HandledTask BEFORE any Stream.Write committs bytes.
        //
        // Test strategy: prime an active stream entry via the first packet, then
        // capture the inner read stream and clear _activeStreams (simulating a
        // racing eviction). Send a second packet — the orphaned read stream's
        // TotalBytesWritten must be unchanged from the first-packet baseline.

        var processor = BuildProcessor();

        var sequenceId = Guid.NewGuid().ToString();
        var firstHeaders = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = sequenceId,
            [HeaderKeys.PacketNumber] = "0",
        };
        var firstEnvelope = new Envelope { Headers = firstHeaders, Body = new byte[] { 0x91, 0x92, 0x93 } };
        await processor.ProcessAsync(new byte[] { 0x91, 0x92, 0x93 }, typeof(byte[]), null, firstHeaders, firstEnvelope);

        // Reach into _activeStreams via reflection to capture the read stream and
        // simulate eviction by clearing the dictionary BEFORE the second packet runs.
        var activeStreamsField = typeof(StreamProcessor).GetField("_activeStreams",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var activeStreams = activeStreamsField.GetValue(processor)!;

        // The dictionary's value type is a private record; reflect through it to grab Stream.
        var indexer = activeStreams.GetType().GetProperty("Item", [typeof(string)])!;
        var entryBefore = indexer.GetValue(activeStreams, [sequenceId])!;
        var streamProperty = entryBefore.GetType().GetProperty("Stream")!;
        var streamBefore = (MessageBusReadStream)streamProperty.GetValue(entryBefore)!;
        var bytesBefore = streamBefore.TotalBytesWritten;
        var packetsBefore = streamBefore.ReceivedPacketCount;

        // Clear the dictionary — simulates the eviction sweep racing right before
        // our touch on the second packet.
        var clearMethod = activeStreams.GetType().GetMethod("Clear")!;
        clearMethod.Invoke(activeStreams, null);

        // Send the second packet. The processor must bail at the touch CAS rather than
        // writing bytes to streamBefore — that stream is no longer indexed, so any write
        // would be lost or land in an evicted-but-still-reachable stream.
        var secondHeaders = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = sequenceId,
            [HeaderKeys.PacketNumber] = "1",
        };
        var secondEnvelope = new Envelope { Headers = secondHeaders, Body = new byte[] { 0x94, 0x95, 0x96 } };
        var result = await processor.ProcessAsync(new byte[] { 0x94, 0x95, 0x96 }, typeof(byte[]), null, secondHeaders, secondEnvelope);

        // The second packet must be acked (idempotent ack on the missing entry) and
        // the orphaned stream must not have received the bytes.
        Assert.Equal(ProcessResult.Handled, result);
        Assert.Equal(bytesBefore, streamBefore.TotalBytesWritten);
        Assert.Equal(packetsBefore, streamBefore.ReceivedPacketCount);
    }

    [Fact]
    public async Task ProcessAsync_TouchSucceeds_WriteCommitsBytes()
    {
        // Sanity check: the happy path still commits bytes when no eviction races.
        var processor = BuildProcessor();

        var sequenceId = Guid.NewGuid().ToString();
        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.MessageType] = HeaderKeys.ByteStream,
            [HeaderKeys.SequenceId] = sequenceId,
            [HeaderKeys.PacketNumber] = "0",
        };
        var envelope = new Envelope { Headers = headers, Body = new byte[] { 0x99, 0x99, 0x99 } };

        var result = await processor.ProcessAsync(new byte[] { 0x99, 0x99, 0x99 }, typeof(byte[]), null, headers, envelope);
        Assert.Equal(ProcessResult.Handled, result);

        // Probe the dict; entry exists with one packet recorded.
        var activeStreamsField = typeof(StreamProcessor).GetField("_activeStreams",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var activeStreams = activeStreamsField.GetValue(processor)!;
        var containsKey = activeStreams.GetType().GetMethod("ContainsKey")!;
        Assert.True((bool)containsKey.Invoke(activeStreams, [sequenceId])!);

        var indexer = activeStreams.GetType().GetProperty("Item", [typeof(string)])!;
        var entry = indexer.GetValue(activeStreams, [sequenceId])!;
        var streamProperty = entry.GetType().GetProperty("Stream")!;
        var stream = (MessageBusReadStream)streamProperty.GetValue(entry)!;
        Assert.Equal(3, stream.TotalBytesWritten);
        Assert.Equal(1, stream.ReceivedPacketCount);
    }
}
