using System.Collections.Concurrent;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

/// <summary>
/// Reassembles byte-stream packets for a single stream sequence into a readable payload.
/// </summary>
/// <remarks>
/// Creates a read stream for the supplied sequence identifier.
/// </remarks>
/// <param name="sequenceId">The identifier shared by all packets in the stream.</param>
/// <param name="maxTotalStreamSize">
/// Upper bound on the cumulative byte count <see cref="Write"/> will admit before throwing
/// <see cref="InvalidOperationException"/>. Defaults to 100 MB; the default preserves
/// historical behaviour for callers that construct the stream directly (tests). Production
/// construction routes through <c>StreamProcessor</c>, which threads the value configured on
/// <c>IBusConfiguration.MaxStreamSizeBytes</c>.
/// </param>
internal sealed class MessageBusReadStream(string sequenceId, long maxTotalStreamSize = 100L * 1024 * 1024) : IMessageBusReadStream
{
    private readonly long _maxTotalStreamSize = maxTotalStreamSize;
    private readonly ConcurrentDictionary<long, byte[]> _packets = new();
    private long _totalBytesWritten;
    // Track received packet count with an atomic counter so IsComplete() is O(1).
    private int _receivedCount;

    // Test-observability hooks. Used by StreamProcessor regression tests to confirm
    // bytes did or did not land in this read stream after a Write. Not part of the
    // public API surface — InternalsVisibleTo gates access.
    internal long TotalBytesWritten => Interlocked.Read(ref _totalBytesWritten);
    internal int ReceivedPacketCount => Volatile.Read(ref _receivedCount);

    /// <inheritdoc />
    public string SequenceId { get; } = sequenceId ?? throw new ArgumentNullException(nameof(sequenceId));
    // -1 = unset. Writes are CAS-from-(-1) so a later (potentially duplicate) close
    // packet cannot shrink or alter an already-set LastPacketNumber; reads use
    // Volatile.Read so concurrent IsComplete checks never see a stale sentinel.
    private long _lastPacketNumber = -1;
    /// <inheritdoc />
    public long LastPacketNumber => Volatile.Read(ref _lastPacketNumber);

    /// <inheritdoc />
    public void SetLastPacketNumber(long lastPacketNumber)
    {
        if (lastPacketNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lastPacketNumber));
        }

        // Pre-CAS validation: any already-received packet that exceeds the proposed
        // LastPacketNumber means the stream is inconsistent regardless of the CAS outcome.
        foreach (var key in _packets.Keys)
        {
            if (key > lastPacketNumber)
            {
                throw new InvalidOperationException(
                    $"Packet number {key} already received for stream {SequenceId} but exceeds " +
                    $"the requested LastPacketNumber {lastPacketNumber}. The stream is inconsistent.");
            }
        }

        var previous = Interlocked.CompareExchange(ref _lastPacketNumber, lastPacketNumber, -1);
        if (previous != -1 && previous != lastPacketNumber)
        {
            throw new InvalidOperationException(
                $"LastPacketNumber already set to {previous}; refusing to overwrite with {lastPacketNumber} for stream {SequenceId}.");
        }

        // Post-CAS re-validation: a concurrent Write that read _lastPacketNumber == -1
        // before our CAS landed may have committed an out-of-range packet between the
        // pre-CAS check and the CAS. Now that _lastPacketNumber is published every
        // future Write rejects, but an in-flight Write that already TryAdd'd is still
        // a violation we surface here. The stream is permanently poisoned at this point;
        // the throw is the right surface (better than producing a silently truncated read).
        foreach (var key in _packets.Keys)
        {
            if (key > lastPacketNumber)
            {
                throw new InvalidOperationException(
                    $"Packet number {key} arrived concurrently and exceeds LastPacketNumber {lastPacketNumber} for stream {SequenceId}. The stream is inconsistent.");
            }
        }
    }

    /// <inheritdoc />
    public void Write(ReadOnlyMemory<byte> data, long packetNumber)
    {
        if (packetNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(packetNumber), packetNumber,
                "Packet number must be non-negative.");
        }

        // Pre-commit upper-bound check: if LastPacketNumber is already set, reject
        // packets above it before we reserve any state.
        var preLast = Volatile.Read(ref _lastPacketNumber);
        if (preLast >= 0 && packetNumber > preLast)
        {
            throw new ArgumentOutOfRangeException(nameof(packetNumber), packetNumber,
                $"Packet number {packetNumber} exceeds LastPacketNumber {preLast} for stream {SequenceId}.");
        }

        // RabbitMQ.Client does not extend the consumer-callback buffer lifetime past
        // the callback return, so we must copy before storing. ToArray() is the copy.
        var stored = data.ToArray();

        // Atomically reserve capacity: if the reservation pushes us past the cap,
        // roll it back before any concurrent writer can observe the inflated total
        // and before we insert into the packet dictionary.
        long newTotal = Interlocked.Add(ref _totalBytesWritten, data.Length);
        if (newTotal > _maxTotalStreamSize)
        {
            Interlocked.Add(ref _totalBytesWritten, -data.Length);
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"Stream exceeds maximum size of {_maxTotalStreamSize:N0} bytes (~{_maxTotalStreamSize / (1024.0 * 1024.0):F1} MB)."));
        }

        if (!_packets.TryAdd(packetNumber, stored))
        {
            // Broker redelivery: the same packet has arrived twice. Roll back the size
            // reservation so the in-memory total mirrors the dictionary's contents and
            // return without throwing; the caller treats this as an idempotent ack.
            // The first payload wins — TryAdd does not overwrite.
            Interlocked.Add(ref _totalBytesWritten, -data.Length);
            return;
        }

        // Post-commit re-check: between the pre-commit Volatile.Read above and the
        // TryAdd, a concurrent SetLastPacketNumber may have published a value that
        // makes our packet out-of-range. Catch that here so the silent-truncation
        // window is closed — symmetric to SetLastPacketNumber's pre+post-CAS validation.
        // TryRemove is safe under concurrent reads: ConcurrentDictionary guarantees
        // atomicity of each individual operation, so a reader either sees this entry
        // or doesn't; there is no torn read.
        var postLast = Volatile.Read(ref _lastPacketNumber);
        if (postLast >= 0 && packetNumber > postLast)
        {
            _packets.TryRemove(packetNumber, out _);
            Interlocked.Add(ref _totalBytesWritten, -data.Length);
            throw new ArgumentOutOfRangeException(nameof(packetNumber), packetNumber,
                $"Packet number {packetNumber} exceeds LastPacketNumber {postLast} (set concurrently) for stream {SequenceId}.");
        }

        // Increment only after the post-commit check so a rolled-back Write does
        // not inflate the count used by IsComplete().
        Interlocked.Increment(ref _receivedCount);
    }

    /// <inheritdoc cref="IMessageBusReadStream.Read"/>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the stream is not yet complete, when the assembled sequence has a missing
    /// packet (packet loss or out-of-order completion signalling), or when the stream's
    /// <see cref="LastPacketNumber"/> becomes unset between the completeness check and assembly.
    /// A missing-packet exception is unrecoverable; treat the stream as corrupt and discard it.
    /// </exception>
    public byte[] Read()
    {
        if (!IsComplete())
        {
            throw new InvalidOperationException("Stream is not yet complete.");
        }

        // Capture LastPacketNumber into a local — defense-in-depth against any future
        // regression that introduces a false-positive IsComplete() return. If the
        // captured snapshot is invalid (e.g. became unset), surface immediately rather
        // than producing a silently truncated read.
        var lastSnapshot = LastPacketNumber;
        if (lastSnapshot < 0)
        {
            throw new InvalidOperationException("Stream LastPacketNumber became unset between IsComplete and Read.");
        }

        // Pre-size MemoryStream to avoid internal buffer doubling.
        var totalBytes = Interlocked.Read(ref _totalBytesWritten);
        using var ms = new MemoryStream(totalBytes > 0 ? (int)totalBytes : 0);
        for (long i = 0; i <= lastSnapshot; i++)
        {
            if (!_packets.TryGetValue(i, out var packet))
            {
                throw new InvalidOperationException(
                    $"Stream {SequenceId} is missing packet {i}; cannot assemble. " +
                    $"This indicates packet loss or out-of-order completion signalling.");
            }
            ms.Write(packet, 0, packet.Length);
        }
        return ms.ToArray();
    }

    /// <inheritdoc cref="IMessageBusReadStream.ReadSequence"/>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the stream is not yet complete, when the assembled sequence has a missing
    /// packet (packet loss or out-of-order completion signalling), or when the stream's
    /// <see cref="LastPacketNumber"/> becomes unset between the completeness check and assembly.
    /// A missing-packet exception is unrecoverable; treat the stream as corrupt and discard it.
    /// </exception>
    public System.Buffers.ReadOnlySequence<byte> ReadSequence()
    {
        if (!IsComplete())
        {
            throw new InvalidOperationException("Stream is not yet complete.");
        }

        // Capture LastPacketNumber into a local — same defense-in-depth as Read.
        var lastSnapshot = LastPacketNumber;
        if (lastSnapshot < 0)
        {
            throw new InvalidOperationException("Stream LastPacketNumber became unset between IsComplete and ReadSequence.");
        }

        // Walk packets 0..lastSnapshot in order, linking them into a ReadOnlySequenceSegment chain.
        PacketSegment? first = null;
        PacketSegment? last = null;
        for (long i = 0; i <= lastSnapshot; i++)
        {
            if (!_packets.TryGetValue(i, out var packet))
            {
                throw new InvalidOperationException(
                    $"Stream {SequenceId} is missing packet {i}; cannot assemble. " +
                    $"This indicates packet loss or out-of-order completion signalling.");
            }

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
        {
            return System.Buffers.ReadOnlySequence<byte>.Empty;
        }

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
