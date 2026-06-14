using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Services.Processors;

internal sealed class StreamProcessor : IMessageProcessor, IAsyncDisposable
{
    private readonly IConsumeScopeAccessor _scopeAccessor;
    private readonly ILogger<StreamProcessor> _logger;
    private readonly IMessageTypeRegistry _typeRegistry;
    private readonly StreamHandlerRegistry _streamHandlerRegistry;
    private readonly IMessageSerializer _serializer;
    private readonly TimeProvider _timeProvider;
    private readonly long _maxStreamSizeBytes;
    private readonly int _maxActiveStreams;
    private readonly ConcurrentDictionary<string, ActiveStreamState> _activeStreams = new(StringComparer.Ordinal);
    // Tracks admitted stream count separately so admission can be gated with Interlocked
    // without relying on ConcurrentDictionary.Count (which is accurate but does not compose
    // atomically with insertion). The counter is incremented before GetOrAdd is called; if
    // the count exceeds the cap we reject without touching the dictionary. If two threads
    // race for the same absent key, the GetOrAdd loser decrements its bump. The counter is
    // also decremented on every eviction, completion, or fault path, keeping it in sync
    // with actual dictionary membership.
    private int _streamCount;
    private int _disposed;
    private readonly ITimer _cleanupTimer;
    /// <summary>
    /// Maximum time a partial stream may sit without new packets before it is evicted.
    /// Tuned to balance memory held by stale streams against transient network stalls.
    /// </summary>
    private static readonly TimeSpan StreamTimeout = TimeSpan.FromMinutes(5);
    /// <summary>Interval at which the sweeper runs to evict stale partial streams.</summary>
    private static readonly TimeSpan StreamCleanupInterval = TimeSpan.FromMinutes(1);
    /// <summary>
    /// Upper bound on LastPacketNumber to prevent attacker-controlled allocation
    /// of unbounded packet-count state.
    /// </summary>
    private const long MaxPacketNumber = 100_000;

    // Cache completed Task<ProcessResult> instances to avoid per-call allocations.
    private static readonly Task<ProcessResult> NotHandledTask = Task.FromResult(ProcessResult.NotHandled);
    private static readonly Task<ProcessResult> HandledTask = Task.FromResult(ProcessResult.Handled);

    public StreamProcessor(
        IConsumeScopeAccessor scopeAccessor,
        ILogger<StreamProcessor> logger,
        IMessageTypeRegistry typeRegistry,
        StreamHandlerRegistry streamHandlerRegistry,
        IMessageSerializer serializer,
        TimeProvider timeProvider,
        IBusConfiguration busConfig)
    {
        _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));
        _logger = logger;
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        _streamHandlerRegistry = streamHandlerRegistry ?? throw new ArgumentNullException(nameof(streamHandlerRegistry));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        // Snapshot the per-stream byte cap and active-stream slot cap at construction
        // time so each fresh MessageBusReadStream and admission check uses the configured
        // ceilings without re-reading IBusConfiguration on every packet. The configuration
        // is frozen by the time the processor is resolved from DI, so the snapshots are
        // final. The active-stream cap defends against DoS via stream-slot exhaustion.
        ArgumentNullException.ThrowIfNull(busConfig);
        _maxStreamSizeBytes = busConfig.MaxStreamSizeBytes;
        _maxActiveStreams = busConfig.MaxActiveStreams;
        _cleanupTimer = _timeProvider.CreateTimer(_ => EvictStaleStreams(), null, StreamCleanupInterval, StreamCleanupInterval);
    }

    public bool RunBeforeDeserialization => true;

    // Exposed for unit-test observability; not part of the public API.
    internal int ActiveStreamCount => _activeStreams.Count;

    public Task<ProcessResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Reject incoming packets after disposal; avoids unbounded dictionary growth
        // from late-arriving messages that race the DisposeAsync caller.
        if (Volatile.Read(ref _disposed) != 0)
        {
            return NotHandledTask;
        }

        if (!headers.TryGetValue(HeaderKeys.MessageType, out var msgTypeRaw))
        {
            return NotHandledTask;
        }

        var msgType = HeaderDecoder.Decode(msgTypeRaw);
        if (!string.Equals(msgType, HeaderKeys.ByteStream, StringComparison.Ordinal))
        {
            return NotHandledTask;
        }

        if (!headers.TryGetValue(HeaderKeys.SequenceId, out var seqIdRaw))
        {
            return NotHandledTask;
        }

        var sequenceId = HeaderDecoder.Decode(seqIdRaw)!;

        // SequenceId must be a valid GUID to prevent arbitrary-string abuse.
        if (!Guid.TryParse(sequenceId, out _))
        {
            _logger.LogWarning("Stream packet has non-GUID SequenceId '{Value}'; discarding", sequenceId);
            return NotHandledTask;
        }

        if (!headers.TryGetValue(HeaderKeys.PacketNumber, out var pnRaw))
        {
            return NotHandledTask;
        }

        var pnString = HeaderDecoder.Decode(pnRaw);
        if (!long.TryParse(pnString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var packetNumber))
        {
            _logger.LogWarning("Stream packet has invalid PacketNumber header '{Value}'; discarding", pnString);
            return HandledTask; // Handled to prevent infinite requeue
        }

        // Bound packetNumber to the same MaxPacketNumber ceiling enforced on LastPacketNumber.
        // Without this, an attacker-controlled header `PacketNumber: long.MaxValue` lands in
        // MessageBusReadStream's packet dictionary at a sparse key, defeating the contiguous-
        // fill design and forcing a future Read() to iterate from 0 to LastPacketNumber. The
        // total-size cap still bounds memory per stream, but per-stream slot keying becomes
        // arbitrary. Reject negatives for the same reason — MessageBusReadStream addresses
        // packets via a non-negative long index.
        if (packetNumber is < 0 or > MaxPacketNumber)
        {
            _logger.LogWarning("Stream packet PacketNumber {Value} out of range (0..{Max}); discarding", packetNumber, MaxPacketNumber);
            return HandledTask;
        }

        // Admission gate: the Interlocked counter is the source of truth. We only call
        // GetOrAdd after a successful counter bump, eliminating the residual race where a
        // speculative GetOrAdd → TryRemove rollback briefly admitted a rejected entry that
        // a concurrent packet for the same sequenceId could observe as live.
        //
        // If two threads race for the same absent sequenceId, both increment the counter;
        // the loser of GetOrAdd decrements its bump. No rejected state ever appears in the
        // dictionary — the counter gate fires before any insertion is attempted.
        if (!_activeStreams.TryGetValue(sequenceId, out _))
        {
            var newCount = Interlocked.Increment(ref _streamCount);
            if (newCount > _maxActiveStreams)
            {
                Interlocked.Decrement(ref _streamCount);
                _logger.LogWarning("Active stream cap {Cap} reached; rejecting new stream {SequenceId}", _maxActiveStreams, sequenceId);
                return NotHandledTask;
            }

            var fresh = new ActiveStreamState(new MessageBusReadStream(sequenceId, _maxStreamSizeBytes), _timeProvider.GetUtcNow());
            var actual = _activeStreams.GetOrAdd(sequenceId, fresh);
            if (!ReferenceEquals(actual, fresh))
            {
                // Lost the absent-key race to another thread that admitted first;
                // roll back our slot reservation since we didn't materialise a new entry.
                Interlocked.Decrement(ref _streamCount);
            }
        }

        // Touch BEFORE Write: a Write-then-touch order would let EvictStaleStreams TryRemove
        // race between Stream.Write and the CAS, leaving packet bytes committed to a
        // now-orphaned MessageBusReadStream with no lookup path. Touch first, bail if
        // evicted, Write only on a freshly-touched entry.
        ActiveStreamState state;
        try
        {
            // Touch: replace the dict entry with a new ActiveStreamState carrying a fresh
            // LastSeenUtc. EvictStaleStreams' TryRemove(KVP) compares records by structural
            // equality; mutating LastSeenUtc in place would leave the record structurally
            // equal and defeat that check, which is why we replace the entry instead.
            while (true)
            {
                if (!_activeStreams.TryGetValue(sequenceId, out var current))
                {
                    // Eviction or completion-dispatch removed the entry between admission
                    // and our touch. Idempotent ack — broker redelivery re-admits a fresh
                    // entry on the next packet. Critically, no Stream.Write yet, so no
                    // bytes are committed to an orphaned MessageBusReadStream.
                    return HandledTask;
                }
                var refreshed = current with { LastSeenUtc = _timeProvider.GetUtcNow() };
                if (_activeStreams.TryUpdate(sequenceId, refreshed, current))
                {
                    state = refreshed;
                    break;
                }
            }

            // Validate the LastPacketNumber header BEFORE writing any bytes; an
            // attacker-controlled header above the cap or unparseable must reject the
            // packet without committing bytes. On rejection evict the active stream
            // entry so the slot reclaims immediately rather than waiting for the
            // 5-minute eviction sweep.
            if (!TryReadLastPacketNumber(headers, sequenceId, out var validatedLastPacketNumber))
            {
                EvictActiveStream(sequenceId);
                return HandledTask;
            }

            // Write commits packet bytes only after we hold a touched entry. A late
            // eviction between this CAS and the Write is bounded — bytes still land in
            // a stream instance that was indexed at touch time, and the eviction sweep's
            // next pass skips this entry because LastSeenUtc was just refreshed.
            state.Stream.Write(messageBytes, packetNumber);

            if (validatedLastPacketNumber is { } lpn)
            {
                state.Stream.SetLastPacketNumber(lpn);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A poison packet wedges the sequence — successive packets keep re-throwing
            // on the same violated invariant (size cap, packet > LastPacketNumber, or
            // LastPacketNumber re-set with a different value). Drop the entry so the
            // next packet starts a fresh sequence rather than waiting for the 5-minute
            // sweep.
            _logger.LogWarning(ex,
                "Stream {SequenceId} faulted on packet {PacketNumber}; evicting partial state",
                sequenceId, packetNumber);
            // Key-only remove (not KVP): a concurrent touch may have replaced the dict entry
            // since our GetOrAdd, but the underlying MessageBusReadStream is the same broken
            // instance (the record's Stream property carries forward across `with`). The
            // sequence is poisoned regardless of which state instance is currently in the
            // dict, so we evict by key rather than by reference.
            if (_activeStreams.TryRemove(sequenceId, out _))
            {
                Interlocked.Decrement(ref _streamCount);
            }

            return HandledTask;
        }

        if (state.Stream.IsComplete())
        {
            return TryDispatchCompletedStream(state, sequenceId, headers, cancellationToken);
        }

        return HandledTask;
    }

    // Resolves headers/handler, claims dispatch via CAS, and invokes the handler. Split
    // out of ProcessAsync to keep that method under the analyzer line-count threshold;
    // the dispatch lifecycle (poison-evict, claim, throw-clear-flag, success-remove) is
    // its own concern and is easier to reason about in isolation.
    private Task<ProcessResult> TryDispatchCompletedStream(
        ActiveStreamState state,
        string sequenceId,
        IDictionary<string, object> headers,
        CancellationToken cancellationToken)
    {
        // Resolve headers and handler BEFORE claiming dispatch. Failures here are poison
        // (no handler registered, type resolution failed) — evict and idempotent-ack so
        // successive packets / redeliveries don't re-buffer the same broken stream.
        if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var ftnRaw))
        {
            _logger.LogWarning("Completed stream {SequenceId} missing FullTypeName header", sequenceId);
            EvictActiveStream(sequenceId);
            return HandledTask;
        }

        var fullTypeName = HeaderDecoder.Decode(ftnRaw);
        if (!_typeRegistry.TryResolve(fullTypeName!, out var resolvedType))
        {
            _logger.LogWarning("Unregistered type '{TypeName}' for completed stream. Rejecting", fullTypeName);
            EvictActiveStream(sequenceId);
            return HandledTask;
        }

        if (!_streamHandlerRegistry.TryGet(resolvedType, out var descriptor))
        {
            _logger.LogWarning("No IStreamHandler registered for {MessageType}", resolvedType.FullName);
            EvictActiveStream(sequenceId);
            return HandledTask;
        }

        var handler = _scopeAccessor.Current.GetService(descriptor.HandlerInterfaceType);
        if (handler == null)
        {
            _logger.LogWarning("No IStreamHandler registered for {MessageType}", resolvedType.FullName);
            EvictActiveStream(sequenceId);
            return HandledTask;
        }

        // Claim dispatch via CAS on DispatchInFlight. Two concurrent final-packet
        // deliveries (e.g. broker redelivery via connection recovery while the original
        // is still running) race here; the loser idempotent-acks. We refresh
        // LastSeenUtc on the claim so the eviction sweep cannot reclaim the entry while
        // a long-running handler holds it — the sweep also skips DispatchInFlight=true
        // entries explicitly, this is belt-and-braces for the sweep's value snapshot.
        if (state.DispatchInFlight)
        {
            return HandledTask;
        }

        var inFlight = state with { DispatchInFlight = true, LastSeenUtc = _timeProvider.GetUtcNow() };
        if (!_activeStreams.TryUpdate(sequenceId, inFlight, state))
        {
            // Lost CAS: a concurrent touch or dispatch claim changed the entry. The
            // winner is responsible for the dispatch; we idempotent-ack.
            return HandledTask;
        }

        // The serializer's ReadOnlySequence overload reads across segments via
        // Utf8JsonReader without flattening — keeps the streaming path zero-copy.
        var assembledSequence = state.Stream.ReadSequence();
        var originalMessage = _serializer.Deserialize(in assembledSequence, resolvedType);

        return InvokeHandlerAsync(descriptor, handler, originalMessage!, state.Stream, sequenceId, cancellationToken);
    }

    private void EvictStaleStreams()
    {
        var cutoff = _timeProvider.GetUtcNow() - StreamTimeout;
        foreach (var kvp in _activeStreams)
        {
            // Skip entries whose handler is actively dispatching: those are not stale
            // partial streams, they're complete streams with an in-flight handler, and the
            // dispatch path is the only writer that should remove them (on success) or
            // clear the flag (on throw, to allow redelivery).
            if (kvp.Value.DispatchInFlight)
            {
                continue;
            }
            if (kvp.Value.LastSeenUtc < cutoff)
            {
                if (_activeStreams.TryRemove(kvp))
                {
                    Interlocked.Decrement(ref _streamCount);
                    _logger.LogWarning("Evicted incomplete stream {SequenceId} after timeout", kvp.Key);
                }
            }
        }
    }

    /// <summary>
    /// Reads and validates the <c>LastPacketNumber</c> header. Returns <see langword="false"/>
    /// when the header is present but unparseable or above <see cref="MaxPacketNumber"/>;
    /// the caller treats false as a rejection signal and evicts the stream entry. Returns
    /// <see langword="true"/> when the header is absent (<paramref name="value"/>=null) or
    /// successfully parsed (<paramref name="value"/>=parsed value).
    /// </summary>
    private bool TryReadLastPacketNumber(IDictionary<string, object> headers, string sequenceId, out long? value)
    {
        value = null;
        if (!headers.TryGetValue(HeaderKeys.LastPacketNumber, out var lpnRaw))
        {
            return true;
        }

        var lpnString = HeaderDecoder.Decode(lpnRaw);
        if (!long.TryParse(lpnString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastPacketNumber))
        {
            _logger.LogWarning("Stream packet has invalid LastPacketNumber header '{Value}'; discarding", lpnString);
            return false;
        }

        if (lastPacketNumber > MaxPacketNumber)
        {
            _logger.LogWarning("Stream {SequenceId} LastPacketNumber {Value} exceeds maximum {Max}; discarding", sequenceId, lastPacketNumber, MaxPacketNumber);
            return false;
        }

        value = lastPacketNumber;
        return true;
    }

    /// <summary>
    /// Removes the active-stream entry for <paramref name="sequenceId"/> and decrements the
    /// admission counter so the slot is reclaimed for new streams immediately. Idempotent —
    /// a no-op if the entry was already removed.
    /// </summary>
    private void EvictActiveStream(string sequenceId)
    {
        if (_activeStreams.TryRemove(sequenceId, out _))
        {
            Interlocked.Decrement(ref _streamCount);
        }
    }

    private async Task<ProcessResult> InvokeHandlerAsync(
        StreamHandlerDescriptor descriptor,
        object handler,
        object originalMessage,
        IMessageBusReadStream stream,
        string sequenceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await descriptor.InvokeExecuteAsync(handler, originalMessage, stream, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancellation: clear the dispatch flag so a fresh dispatch can
            // retry the stream against the already-assembled prior packets, then
            // rethrow with the caller's token. The broker will redeliver the final
            // packet; we do not lose the assembled state.
            ClearDispatchFlag(sequenceId);
            cancellationToken.ThrowIfCancellationRequested();
            throw; // unreachable but keeps the compiler happy
        }
        catch (OperationCanceledException ex)
        {
            // OCE that the caller's CT did NOT request — almost always a handler's
            // own linked CTS firing. Treat as a handler failure: log, clear the
            // dispatch flag, and rethrow with the caller's token so the dispatch
            // pipeline's `when (cancellationToken.IsCancellationRequested)` gate
            // evaluates correctly. Rethrowing the original would carry the handler's
            // unrelated token; the downstream metrics pipeline gates cancelled-
            // classification on the caller's CT, so token identity matters.
            _logger.LogError(ex,
                "Stream handler {HandlerType} threw OperationCanceledException with an unrelated CT for stream {SequenceId}",
                handler.GetType().FullName, sequenceId);
            ClearDispatchFlag(sequenceId);
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (Exception ex)
        {
            // Handler threw — the broker redelivers the final packet. Leave the entry in
            // place so the redelivery re-invokes the handler against the already-assembled
            // stream rather than starting over with only the final packet (which would be
            // unrecoverable data loss). Clear the in-flight flag so the next dispatch can
            // claim. The eviction sweep skips DispatchInFlight=true entries, and we
            // refreshed LastSeenUtc at claim time, so the entry has another StreamTimeout
            // window after this throw before the sweep can reclaim it.
            _logger.LogError(ex,
                "Stream handler {HandlerType} threw for stream {SequenceId}; clearing dispatch flag for redelivery",
                handler.GetType().FullName, sequenceId);
            ClearDispatchFlag(sequenceId);
            throw;
        }

        // Handler succeeded — remove the entry. Key-based TryRemove (not value-based)
        // because a late packet that touched the entry during handler execution refreshed
        // LastSeenUtc, producing a different ActiveStreamState record; a value-comparing
        // remove would miss that and leak the entry / counter slot. Touch preserves
        // DispatchInFlight=true (record-copy), so the eviction sweep already skipped this
        // entry, and the only writer that removes is this success path — making key-based
        // removal safe from double-decrement.
        if (_activeStreams.TryRemove(sequenceId, out _))
        {
            Interlocked.Decrement(ref _streamCount);
        }
        return ProcessResult.Handled;
    }

    // Best-effort clear of the DispatchInFlight flag after handler cancellation or throw.
    // Loops to absorb concurrent touch updates that preserve DispatchInFlight via record-
    // copy. A concurrent eviction (the sweep skips DispatchInFlight=true entries, but a
    // disposal could clear the dictionary) means TryGetValue returns false and we no-op.
    private void ClearDispatchFlag(string sequenceId)
    {
        while (_activeStreams.TryGetValue(sequenceId, out var current))
        {
            if (!current.DispatchInFlight)
            {
                return;
            }
            var cleared = current with { DispatchInFlight = false };
            if (_activeStreams.TryUpdate(sequenceId, cleared, current))
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cleanupTimer.DisposeAsync().ConfigureAwait(false);

        // Drain in-flight stream entries; MessageBusReadStream does not implement
        // IDisposable, so clearing the dictionary is sufficient for GC reclamation.
        _activeStreams.Clear();
        Interlocked.Exchange(ref _streamCount, 0);
    }

    // Immutable so updates require a new instance via ConcurrentDictionary.TryUpdate;
    // the eviction sweep's KVP-based TryRemove compares records by structural equality,
    // so a concurrent touch produces an unequal record and the sweep no-ops on the stale
    // value (in-place mutation would stay structurally equal and defeat the check).
    //
    // DispatchInFlight: latched true under CAS by the dispatcher when a complete stream is
    // about to invoke the handler; preserved across touch (record-copy) so a concurrent
    // packet's touch does not race-clear it; cleared on handler throw / cancel so a
    // redelivery can re-invoke against the already-assembled stream rather than losing
    // the prior packets.
    private sealed record ActiveStreamState(MessageBusReadStream Stream, DateTimeOffset LastSeenUtc, bool DispatchInFlight = false);
}
