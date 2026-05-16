using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

/// <summary>
/// Splits a large payload into stream packets and sends them through the configured producer.
/// </summary>
internal sealed class MessageBusWriteStream : IMessageBusWriteStream
{
    private readonly IProducer _producer;
    private readonly string _endpoint;
    private readonly Type _messageType;
    private readonly TimeProvider _timeProvider;
    private readonly string _sequenceId;
    private readonly Dictionary<string, string> _baseHeaders;
    private long _packetNumber;
    // Set only after a CloseAsync's SendBytesAsync returns successfully. The durable
    // success indicator: idempotent CloseAsync callers short-circuit on this; a transient
    // close-packet send failure leaves _closedFlag=0 so a retry can re-enter and complete.
    private int _closedFlag;
    // Set the moment a close attempt begins. Never reset. WriteAsync rejects with
    // ObjectDisposedException once this is 1, even if the close itself failed — a
    // stream that began closing cannot un-close (mirrors the fault-flag's permanence).
    private int _closeStarted;
    // Single-flight gate over the drain+send body. CAS 0->1 to enter; reset to 0 in a
    // finally block. On success _closedFlag=1 already short-circuits new entries; on
    // failure resetting to 0 lets a retry re-enter and try the close-packet send again.
    private int _closeInProgress;
    // 0 = healthy, 1 = a SendBytesAsync call has thrown. Once faulted, WriteAsync refuses
    // to consume another packet number — any subsequent send would land beyond the stranded
    // number and create a permanent gap the reader can never close.
    private int _faulted;
    // Track in-flight writes so CloseAsync can drain them before reading _packetNumber
    // for the close packet. Without the drain, a writer that cleared the _closedFlag check
    // but hadn't yet Interlocked.Increment-ed would publish *after* the close packet with
    // a number past LastPacketNumber — the reader drops it.
    private int _inFlightWrites;
    // Default close budget used in production. The instance field allows tests to
    // inject a short value via the internal constructor without affecting other instances.
    private static readonly TimeSpan DefaultCloseDrainTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _closeDrainTimeout;

    /// <summary>
    /// Creates a write stream that targets a single endpoint and message type.
    /// Uses <see cref="TimeProvider.System"/> for the close-drain deadline.
    /// </summary>
    /// <param name="producer">The producer used to send stream packets.</param>
    /// <param name="endpoint">The destination endpoint for the stream.</param>
    /// <param name="messageType">The logical message type represented by the stream.</param>
    public MessageBusWriteStream(IProducer producer, string endpoint, Type messageType)
        : this(producer, endpoint, messageType, TimeProvider.System) { }

    /// <summary>
    /// Creates a write stream that targets a single endpoint and message type.
    /// </summary>
    /// <param name="producer">The producer used to send stream packets.</param>
    /// <param name="endpoint">The destination endpoint for the stream.</param>
    /// <param name="messageType">The logical message type represented by the stream.</param>
    /// <param name="timeProvider">
    /// The time provider used to compute the close-drain deadline. Inject a fake
    /// provider in tests to control the timeout without relying on wall-clock time.
    /// </param>
    public MessageBusWriteStream(IProducer producer, string endpoint, Type messageType, TimeProvider timeProvider)
        : this(producer, endpoint, messageType, timeProvider, DefaultCloseDrainTimeout) { }

    /// <summary>
    /// Creates a write stream with an explicit close-drain budget.
    /// Intended for test use to exercise timeout paths without wall-clock waits.
    /// </summary>
    internal MessageBusWriteStream(
        IProducer producer, string endpoint, Type messageType,
        TimeProvider timeProvider, TimeSpan closeDrainTimeout)
    {
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _messageType = messageType ?? throw new ArgumentNullException(nameof(messageType));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _closeDrainTimeout = closeDrainTimeout;
        _sequenceId = FormatGuid(Guid.NewGuid());
        // Type-reserved headers (FullTypeName / TypeName / MessageType) are stamped by
        // the producer from _messageType — they must not be seeded here, since the
        // producer treats them as server-authoritative and overwrites any caller value.
        _baseHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HeaderKeys.SequenceId] = _sequenceId
        };
    }

    /// <inheritdoc />
    public async Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Reserve the in-flight slot BEFORE checking the close flag so that a concurrent
        // CloseAsync observing _inFlightWrites == 0 cannot race past us. Rolled back below
        // if the stream is already closed.
        Interlocked.Increment(ref _inFlightWrites);
        try
        {
            // _closeStarted reflects "a close attempt has begun" (set on CloseAsync entry,
            // never reset). _closedFlag reflects "the close packet was successfully sent"
            // (set only after SendBytesAsync returns). _closeStarted rejects new writes —
            // a close-in-progress whose send hasn't completed must not admit late packets,
            // since the close-packet number is reserved against _packetNumber's current
            // value and a Write after that read would overshoot LastPacketNumber.
            if (Volatile.Read(ref _closeStarted) == 1)
            {
                throw new ObjectDisposedException(nameof(MessageBusWriteStream));
            }

            if (Volatile.Read(ref _faulted) == 1)
            {
                throw new InvalidOperationException(
                    $"Stream {_sequenceId} is faulted from a previous send failure; create a new stream.");
            }

            var packetNum = Interlocked.Increment(ref _packetNumber) - 1;

            try
            {
                // Pre-size the dict to avoid rehash during the copy. A separate dict
                // per packet is required because the producer may mutate / enqueue the
                // dictionary asynchronously, so reuse would race with concurrent writes.
                var headers = new Dictionary<string, string>(_baseHeaders.Count + 1, StringComparer.Ordinal);
                foreach (var kvp in _baseHeaders)
                {
                    headers[kvp.Key] = kvp.Value;
                }

                headers[HeaderKeys.PacketNumber] = FormatInt64(packetNum);

                // ROM<byte> threads directly to SendBytesAsync — no intermediate copy.
                // The buffer is read once; after the await returns the caller is free to reuse it.
                await _producer.SendBytesAsync(_endpoint, _messageType, buffer, headers, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // User-driven cancellation must NOT latch _faulted. The packet number is
                // stranded, but the caller will not call CloseAsync if they cancelled the
                // write — they discard the stream. If they DO call CloseAsync afterwards
                // (e.g. cleanup in a finally), latching _faulted would force CloseAsync to
                // skip the close packet, leaving the receiver's MessageBusReadStream
                // perpetually incomplete until the 5-minute eviction sweep. Re-throw so the
                // caller learns the write was cancelled.
                throw;
            }
            catch
            {
                // The reserved packet number is now stranded — there is no safe way for the
                // caller to retry without producing a permanent gap, so refuse all further
                // writes. The exception still propagates so the caller learns the send failed.
                // See learn/operations/cancellation: any throw between Increment and SendBytesAsync,
                // including OOM during dict alloc, must trigger the fault flag.
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
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Mark the close as started — WriteAsync will reject once this is 1, regardless
        // of whether the close ultimately succeeds. A stream that began closing cannot
        // un-close: even on a transient send failure, late writes would overshoot the
        // close packet's reserved LastPacketNumber.
        Interlocked.CompareExchange(ref _closeStarted, 1, 0);

        // Idempotent fast-path: a prior CloseAsync already shipped the close packet.
        if (Volatile.Read(ref _closedFlag) == 1)
        {
            return;
        }

        // Single-flight: only one caller runs the drain+send body at a time. A second
        // caller spins until the first either succeeds (_closedFlag=1, return) or fails
        // (_closeInProgress released back to 0 with _closedFlag still 0, retry).
        var entrySpin = new SpinWait();
        while (Interlocked.CompareExchange(ref _closeInProgress, 1, 0) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Volatile.Read(ref _closedFlag) == 1)
            {
                return;
            }

            if (entrySpin.NextSpinWillYield)
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                entrySpin.SpinOnce();
            }
        }

        try
        {
            // A faulted stream has a stranded packet number; emitting a close packet would
            // declare a LastPacketNumber the reader can never reach. Mark closed and return
            // without sending — the caller already received an exception from the failing
            // write that set the fault flag.
            if (Volatile.Read(ref _faulted) == 1)
            {
                Volatile.Write(ref _closedFlag, 1);
                return;
            }

            // Drain in-flight writes before reading _packetNumber. Any WriteAsync that
            // passed its _closeStarted gate must complete (success or exception) before
            // we assign the close-packet number — otherwise its packet would ship with a
            // number beyond LastPacketNumber and the reader would silently drop it.
            var deadline = _timeProvider.GetUtcNow().UtcDateTime + _closeDrainTimeout;
            var drainSpin = new SpinWait();
            while (Volatile.Read(ref _inFlightWrites) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (drainSpin.NextSpinWillYield && _timeProvider.GetUtcNow().UtcDateTime >= deadline)
                {
                    throw new TimeoutException(
                        $"Timed out waiting for {Volatile.Read(ref _inFlightWrites)} in-flight write(s) to drain before closing stream {_sequenceId}.");
                }

                if (drainSpin.NextSpinWillYield)
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    drainSpin.SpinOnce();
                }
            }

            // Re-check the fault flag after the drain. A WriteAsync that started before
            // _closeStarted=1 reserves its packet number via Interlocked.Increment before
            // the SendBytesAsync await; a failure during that await sets _faulted=1 and
            // the outer finally decrements _inFlightWrites. The drain exits cleanly, but
            // _packetNumber now reflects a slot whose packet was never sent. Shipping a
            // close packet with that LastPacketNumber leaves the reader unable to ever
            // satisfy IsComplete (the missing slot is unreachable). Treat post-drain fault
            // the same as pre-drain fault: mark closed and return without sending.
            if (Volatile.Read(ref _faulted) == 1)
            {
                Volatile.Write(ref _closedFlag, 1);
                return;
            }

            // _packetNumber was post-incremented on each WriteAsync, so after N data
            // packets (indices 0..N-1) its value is N. The close packet reuses that value
            // as its own index, and LastPacketNumber equals the count. The reader's
            // IsComplete loop checks 0..LastPacketNumber inclusive so the empty close
            // packet fills that final slot. Changing the close-packet payload in the
            // future would break this invariant — see MessageBusReadStream.Read().
            var packetNum = Interlocked.Read(ref _packetNumber);

            var headers = new Dictionary<string, string>(_baseHeaders.Count + 2, StringComparer.Ordinal);
            foreach (var kvp in _baseHeaders)
            {
                headers[kvp.Key] = kvp.Value;
            }

            var packetNumString = FormatInt64(packetNum);
            headers[HeaderKeys.PacketNumber] = packetNumString;
            headers[HeaderKeys.LastPacketNumber] = packetNumString;

            // Send the close packet. If this throws, _closedFlag stays 0 (the finally
            // block releases _closeInProgress) so a retry can re-enter and try again.
            await _producer.SendBytesAsync(_endpoint, _messageType, ReadOnlyMemory<byte>.Empty, headers, cancellationToken).ConfigureAwait(false);

            // Success: durable close. WriteAsync's _closeStarted gate already locks out
            // late writes; setting _closedFlag now lets idempotent CloseAsync callers
            // short-circuit without re-entering the in-progress gate.
            Volatile.Write(ref _closedFlag, 1);
        }
        finally
        {
            // Release the in-progress gate. On failure this lets a retry re-enter; on
            // success _closedFlag=1 already short-circuits new entries.
            Volatile.Write(ref _closeInProgress, 0);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // CloseAsync with no token leaves the single-flight spin gate running indefinitely
        // if the in-flight CloseAsync holder is wedged. Apply the same _closeDrainTimeout
        // used by the drain itself so the spin cannot park the disposing thread forever.
        using var cts = new CancellationTokenSource(_closeDrainTimeout);
        try
        {
            await CloseAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Best-effort close: the gate holder is wedged. Releasing the stream here
            // matches the pattern other transports use when the close budget elapses.
        }
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
