using System.Collections.Concurrent;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public sealed class MessageBusReadStream : IMessageBusReadStream
{
    private const long MaxTotalStreamSize = 100 * 1024 * 1024;
    private readonly ConcurrentDictionary<long, byte[]> _packets = new();
    private long _totalBytesWritten;
    // P-027: track received packet count with an atomic counter so IsComplete() is O(1).
    private int _receivedCount;

    public MessageBusReadStream(string sequenceId)
    {
        SequenceId = sequenceId ?? throw new ArgumentNullException(nameof(sequenceId));
    }

    public string SequenceId { get; }
    public long LastPacketNumber { get; private set; } = -1;

    public void SetLastPacketNumber(long lastPacketNumber)
    {
        if (lastPacketNumber < 0) throw new ArgumentOutOfRangeException(nameof(lastPacketNumber));
        LastPacketNumber = lastPacketNumber;
    }

    public void Write(byte[] data, long packetNumber)
    {
        // Atomically reserve capacity: if the reservation pushes us past the cap,
        // roll it back before any concurrent writer can observe the inflated total
        // and before we insert into the packet dictionary (C-03).
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
        // P-027: increment after a successful add so IsComplete() can compare counts.
        Interlocked.Increment(ref _receivedCount);
    }

    public byte[] Read()
    {
        if (!IsComplete())
            throw new InvalidOperationException("Stream is not yet complete.");

        // P-028: pre-size MemoryStream to avoid internal buffer doubling.
        var totalBytes = Interlocked.Read(ref _totalBytesWritten);
        using var ms = new MemoryStream(totalBytes > 0 ? (int)totalBytes : 0);
        for (long i = 0; i <= LastPacketNumber; i++)
        {
            if (_packets.TryGetValue(i, out var packet))
                ms.Write(packet, 0, packet.Length);
        }
        return ms.ToArray();
    }

    public bool IsComplete()
    {
        // P-027: O(1) check — compare received packet count against expected count.
        return LastPacketNumber >= 0 && _receivedCount == LastPacketNumber + 1;
    }
}
