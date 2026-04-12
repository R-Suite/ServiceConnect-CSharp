using System.Collections.Concurrent;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public sealed class MessageBusReadStream : IMessageBusReadStream
{
    private const long MaxTotalStreamSize = 100 * 1024 * 1024;
    private readonly ConcurrentDictionary<long, byte[]> _packets = new();
    private long _totalBytesWritten;

    public string SequenceId { get; set; } = string.Empty;
    public long LastPacketNumber { get; set; } = -1;
    public MessageBusStreamComplete CompleteEventHandler { get; set; } = null!;
    public int HandlerCount { get; set; }

    public void Write(byte[] data, long packetNumber)
    {
        if (Interlocked.Add(ref _totalBytesWritten, data.Length) > MaxTotalStreamSize)
            throw new InvalidOperationException($"Stream exceeds maximum size of {MaxTotalStreamSize / (1024 * 1024)} MB.");
        _packets[packetNumber] = data;
    }

    public byte[] Read()
    {
        if (!IsComplete())
            throw new InvalidOperationException("Stream is not yet complete.");

        using var ms = new MemoryStream();
        for (long i = 0; i <= LastPacketNumber; i++)
        {
            if (_packets.TryGetValue(i, out var packet))
                ms.Write(packet, 0, packet.Length);
        }
        return ms.ToArray();
    }

    public bool IsComplete()
    {
        if (LastPacketNumber < 0) return false;
        for (long i = 0; i <= LastPacketNumber; i++)
        {
            if (!_packets.ContainsKey(i)) return false;
        }
        return true;
    }
}
