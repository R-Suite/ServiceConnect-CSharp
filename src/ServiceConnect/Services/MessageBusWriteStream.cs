using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

/// <summary>
/// Splits a large payload into stream packets and sends them through the configured producer.
/// </summary>
public sealed class MessageBusWriteStream : IMessageBusWriteStream
{
    private readonly IProducer _producer;
    private readonly string _endpoint;
    private readonly Type _messageType;
    private readonly string _sequenceId;
    private readonly Dictionary<string, string> _baseHeaders;
    private long _packetNumber;
    private int _closedFlag;
    // 0 = healthy, 1 = a SendBytesAsync call has thrown. Once faulted, WriteAsync refuses
    // to consume another packet number — a successful retry would land beyond the missing
    // packet and create a permanent gap the reader can never close.
    private int _faulted;
    // Track in-flight writes so CloseAsync can drain them before reading _packetNumber
    // for the close packet. Without the drain, a writer that cleared the _closedFlag check
    // but hadn't yet Interlocked.Increment-ed would publish *after* the close packet with
    // a number past LastPacketNumber — the reader drops it.
    private int _inFlightWrites;
    private static readonly TimeSpan CloseDrainTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Creates a write stream that targets a single endpoint and message type.
    /// </summary>
    /// <param name="producer">The producer used to send stream packets.</param>
    /// <param name="endpoint">The destination endpoint for the stream.</param>
    /// <param name="messageType">The logical message type represented by the stream.</param>
    public MessageBusWriteStream(IProducer producer, string endpoint, Type messageType)
    {
        _producer = producer;
        _endpoint = endpoint;
        _messageType = messageType;
        _sequenceId = FormatGuid(Guid.NewGuid());
        // Type-reserved headers (FullTypeName / TypeName / MessageType) are stamped by
        // the producer from _messageType — they must not be seeded here, since the
        // producer treats them as server-authoritative and overwrites any caller value.
        _baseHeaders = new Dictionary<string, string>
        {
            [HeaderKeys.SequenceId] = _sequenceId
        };
    }

    /// <inheritdoc />
    public async Task WriteAsync(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if ((uint)offset > (uint)buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if ((uint)count > (uint)(buffer.Length - offset))
            throw new ArgumentOutOfRangeException(nameof(count));

        // Reserve the in-flight slot BEFORE checking the close flag so that a concurrent
        // CloseAsync observing _inFlightWrites == 0 cannot race past us. Rolled back below
        // if the stream is already closed.
        Interlocked.Increment(ref _inFlightWrites);
        try
        {
            if (Volatile.Read(ref _closedFlag) == 1)
                throw new ObjectDisposedException(nameof(MessageBusWriteStream));
            if (Volatile.Read(ref _faulted) == 1)
                throw new InvalidOperationException(
                    $"Stream {_sequenceId} is faulted from a previous send failure; create a new stream.");

            var packet = new byte[count];
            Array.Copy(buffer, offset, packet, 0, count);

            var packetNum = Interlocked.Increment(ref _packetNumber) - 1;

            // Pre-size the dict to avoid rehash during the copy. A separate dict
            // per packet is required because the producer may mutate / enqueue the
            // dictionary asynchronously, so reuse would race with concurrent writes.
            var headers = new Dictionary<string, string>(_baseHeaders.Count + 1);
            foreach (var kvp in _baseHeaders) headers[kvp.Key] = kvp.Value;
            headers[HeaderKeys.PacketNumber] = FormatInt64(packetNum);

            try
            {
                await _producer.SendBytesAsync(_endpoint, _messageType, packet, headers).ConfigureAwait(false);
            }
            catch
            {
                // The reserved packet number is now stranded — there is no safe way for the
                // caller to retry without producing a permanent gap, so refuse all further
                // writes. The exception still propagates so the caller learns the send failed.
                Volatile.Write(ref _faulted, 1);
                throw;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightWrites);
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync()
    {
        if (Interlocked.CompareExchange(ref _closedFlag, 1, 0) != 0) return;

        // A faulted stream has a stranded packet number; emitting a close packet would
        // declare a LastPacketNumber the reader can never reach. Swallow the close
        // request silently — the caller already received an exception from the failing
        // write that set the fault flag.
        if (Volatile.Read(ref _faulted) == 1) return;

        // Drain in-flight writes before reading _packetNumber. Any WriteAsync that passed
        // its closed-flag check must complete (either successfully or with an exception)
        // before we assign the close packet number — otherwise its packet would ship with
        // a number beyond LastPacketNumber and the reader would silently drop it.
        var deadline = DateTime.UtcNow + CloseDrainTimeout;
        var spin = new SpinWait();
        while (Volatile.Read(ref _inFlightWrites) > 0)
        {
            if (spin.NextSpinWillYield && DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"Timed out waiting for {Volatile.Read(ref _inFlightWrites)} in-flight write(s) to drain before closing stream {_sequenceId}.");

            if (spin.NextSpinWillYield)
                await Task.Delay(10).ConfigureAwait(false);
            else
                spin.SpinOnce();
        }

        // _packetNumber was post-incremented on each WriteAsync, so after N data
        // packets (indices 0..N-1) its value is N. The close packet reuses that value
        // as its own index, and LastPacketNumber equals the count. The reader's
        // IsComplete loop checks 0..LastPacketNumber inclusive so the empty close
        // packet fills that final slot. Changing the close-packet payload in the
        // future would break this invariant — see MessageBusReadStream.Read().
        var packetNum = Interlocked.Read(ref _packetNumber);

        var headers = new Dictionary<string, string>(_baseHeaders.Count + 2);
        foreach (var kvp in _baseHeaders) headers[kvp.Key] = kvp.Value;
        var packetNumString = FormatInt64(packetNum);
        headers[HeaderKeys.PacketNumber] = packetNumString;
        headers[HeaderKeys.LastPacketNumber] = packetNumString;

        await _producer.SendBytesAsync(_endpoint, _messageType, [], headers).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }

    private static string FormatGuid(Guid value)
    {
        Span<char> buffer = stackalloc char[36];
        value.TryFormat(buffer, out var charsWritten);
        return new string(buffer[..charsWritten]);
    }

    private static string FormatInt64(long value)
    {
        Span<char> buffer = stackalloc char[20];
        value.TryFormat(buffer, out var charsWritten);
        return new string(buffer[..charsWritten]);
    }
}
