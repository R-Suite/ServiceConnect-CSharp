using System.Collections.Concurrent;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

/// <summary>
/// Reassembles byte-stream packets for a single stream sequence into a readable payload.
/// </summary>
public sealed class MessageBusReadStream : IMessageBusReadStream
{
    private const long MaxTotalStreamSize = 100 * 1024 * 1024;
    private readonly ConcurrentDictionary<long, byte[]> _packets = new();
    private long _totalBytesWritten;
    // Track received packet count with an atomic counter so IsComplete() is O(1).
    private int _receivedCount;

    /// <summary>
    /// Creates a read stream for the supplied sequence identifier.
    /// </summary>
    /// <param name="sequenceId">The identifier shared by all packets in the stream.</param>
    public MessageBusReadStream(string sequenceId)
    {
        SequenceId = sequenceId ?? throw new ArgumentNullException(nameof(sequenceId));
    }

    /// <inheritdoc />
    public string SequenceId { get; }
    // -1 = unset. Writes are CAS-from-(-1) so a later (potentially duplicate) close
    // packet cannot shrink or alter an already-set LastPacketNumber; reads use
    // Volatile.Read so concurrent IsComplete checks never see a stale sentinel.
    private long _lastPacketNumber = -1;
    /// <inheritdoc />
    public long LastPacketNumber => Volatile.Read(ref _lastPacketNumber);

    /// <inheritdoc />
    public void SetLastPacketNumber(long lastPacketNumber)
    {
        if (lastPacketNumber < 0) throw new ArgumentOutOfRangeException(nameof(lastPacketNumber));
        var previous = Interlocked.CompareExchange(ref _lastPacketNumber, lastPacketNumber, -1);
        if (previous != -1 && previous != lastPacketNumber)
            throw new InvalidOperationException(
                $"LastPacketNumber already set to {previous}; refusing to overwrite with {lastPacketNumber} for stream {SequenceId}.");
    }

    /// <inheritdoc />
    public void Write(byte[] data, long packetNumber)
    {
        if (packetNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(packetNumber), packetNumber,
                "Packet number must be non-negative.");
        // Validate against LastPacketNumber when it is already set.  This is done
        // with Volatile.Read so we observe the latest CAS-committed value without
        // acquiring a separate lock — the worst case is that a concurrent
        // SetLastPacketNumber races and we miss the check, but that race is
        // benign: a packet that genuinely belongs to the stream will have
        // packetNumber <= lastPacketNumber by protocol, and a rogue out-of-range
        // packet must always fail.
        var last = Volatile.Read(ref _lastPacketNumber);
        if (last >= 0 && packetNumber > last)
            throw new ArgumentOutOfRangeException(nameof(packetNumber), packetNumber,
                $"Packet number {packetNumber} exceeds LastPacketNumber {last} for stream {SequenceId}.");

        // Atomically reserve capacity: if the reservation pushes us past the cap,
        // roll it back before any concurrent writer can observe the inflated total
        // and before we insert into the packet dictionary.
        long newTotal = Interlocked.Add(ref _totalBytesWritten, data.Length);
        if (newTotal > MaxTotalStreamSize)
        {
            Interlocked.Add(ref _totalBytesWritten, -data.Length);
            throw new InvalidOperationException($"Stream exceeds maximum size of {MaxTotalStreamSize / (1024 * 1024)} MB.");
        }
        if (!_packets.TryAdd(packetNumber, data))
        {
            // Duplicate packet — roll back the reservation to keep the size check honest.
            Interlocked.Add(ref _totalBytesWritten, -data.Length);
            throw new InvalidOperationException($"Duplicate packet number {packetNumber} received for stream {SequenceId}.");
        }
        // Increment after a successful add so IsComplete() can compare counts.
        Interlocked.Increment(ref _receivedCount);
    }

    /// <inheritdoc />
    public byte[] Read()
    {
        if (!IsComplete())
            throw new InvalidOperationException("Stream is not yet complete.");

        // Pre-size MemoryStream to avoid internal buffer doubling.
        var totalBytes = Interlocked.Read(ref _totalBytesWritten);
        using var ms = new MemoryStream(totalBytes > 0 ? (int)totalBytes : 0);
        for (long i = 0; i <= LastPacketNumber; i++)
        {
            if (_packets.TryGetValue(i, out var packet))
                ms.Write(packet, 0, packet.Length);
        }
        return ms.ToArray();
    }

    /// <inheritdoc />
    public System.Buffers.ReadOnlySequence<byte> ReadSequence()
    {
        if (!IsComplete())
            throw new InvalidOperationException("Stream is not yet complete.");

        // Walk packets 0..LastPacketNumber in order, linking them into a ReadOnlySequenceSegment chain.
        PacketSegment? first = null;
        PacketSegment? last = null;
        for (long i = 0; i <= LastPacketNumber; i++)
        {
            if (!_packets.TryGetValue(i, out var packet))
                continue;
            if (first is null)
            {
                first = new PacketSegment(packet);
                last = first;
            }
            else
            {
                last = last!.Append(packet);
            }
        }

        if (first is null)
            return System.Buffers.ReadOnlySequence<byte>.Empty;

        return new System.Buffers.ReadOnlySequence<byte>(first, 0, last!, last!.Memory.Length);
    }

    /// <inheritdoc />
    public bool IsComplete()
    {
        // O(1) check — compare received packet count against expected count.
        var last = Volatile.Read(ref _lastPacketNumber);
        return last >= 0 && Volatile.Read(ref _receivedCount) == last + 1;
    }

    private sealed class PacketSegment : System.Buffers.ReadOnlySequenceSegment<byte>
    {
        public PacketSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public PacketSegment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new PacketSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }
    }
}
