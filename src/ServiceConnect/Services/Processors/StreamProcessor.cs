using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class StreamProcessor : IMessageProcessor, IAsyncDisposable
{
    private readonly ConsumeScopeAccessor _scopeAccessor;
    private readonly ILogger<StreamProcessor> _logger;
    private readonly IMessageTypeRegistry _typeRegistry;
    private readonly StreamHandlerRegistry _streamHandlerRegistry;
    private readonly IMessageSerializer _serializer;
    private readonly TimeProvider _timeProvider;
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
    /// Maximum number of concurrently tracked partial streams.
    /// Prevents DoS via stream slot exhaustion.
    /// </summary>
    private const int MaxActiveStreams = 1000;
    /// <summary>
    /// Upper bound on LastPacketNumber to prevent attacker-controlled allocation
    /// of unbounded packet-count state.
    /// </summary>
    private const long MaxPacketNumber = 100_000;

    // Cache completed Task<ProcessResult> instances to avoid per-call allocations.
    private static readonly Task<ProcessResult> NotHandledTask = Task.FromResult(ProcessResult.NotHandled);
    private static readonly Task<ProcessResult> HandledTask = Task.FromResult(ProcessResult.Handled);

    public StreamProcessor(
        ConsumeScopeAccessor scopeAccessor,
        ILogger<StreamProcessor> logger,
        IMessageTypeRegistry typeRegistry,
        StreamHandlerRegistry streamHandlerRegistry,
        IMessageSerializer serializer,
        TimeProvider timeProvider)
    {
        _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));
        _logger = logger;
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        _streamHandlerRegistry = streamHandlerRegistry ?? throw new ArgumentNullException(nameof(streamHandlerRegistry));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
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
            if (newCount > MaxActiveStreams)
            {
                Interlocked.Decrement(ref _streamCount);
                _logger.LogWarning("Active stream cap {Cap} reached; rejecting new stream {SequenceId}", MaxActiveStreams, sequenceId);
                return NotHandledTask;
            }

            var fresh = new ActiveStreamState(new MessageBusReadStream(sequenceId), _timeProvider.GetUtcNow());
            var actual = _activeStreams.GetOrAdd(sequenceId, fresh);
            if (!ReferenceEquals(actual, fresh))
            {
                // Lost the absent-key race to another thread that admitted first;
                // roll back our slot reservation since we didn't materialise a new entry.
                Interlocked.Decrement(ref _streamCount);
            }
        }

        // Touch BEFORE Write: pre-fix Write-then-touch let EvictStaleStreams TryRemove
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

            // Write commits packet bytes only after we hold a touched entry. A late
            // eviction between this CAS and the Write is bounded — bytes still land in
            // a stream instance that was indexed at touch time, and the eviction sweep's
            // next pass skips this entry because LastSeenUtc was just refreshed.
            state.Stream.Write(messageBytes, packetNumber);

            if (headers.TryGetValue(HeaderKeys.LastPacketNumber, out var lpnRaw))
            {
                var lpnString = HeaderDecoder.Decode(lpnRaw);
                if (!long.TryParse(lpnString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastPacketNumber))
                {
                    _logger.LogWarning("Stream packet has invalid LastPacketNumber header '{Value}'; discarding", lpnString);
                    return HandledTask;
                }
                // Cap LastPacketNumber to prevent attacker-controlled unbounded state.
                if (lastPacketNumber > MaxPacketNumber)
                {
                    _logger.LogWarning("Stream {SequenceId} LastPacketNumber {Value} exceeds maximum {Max}; discarding", sequenceId, lastPacketNumber, MaxPacketNumber);
                    return HandledTask;
                }
                state.Stream.SetLastPacketNumber(lastPacketNumber);
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
            // Race: two final-packet deliveries can both observe IsComplete() == true.
            // Only the caller that wins TryRemove transitions the dict entry from
            // "present" to "removed"; the loser sees a stale state and must idempotent-ack.
            if (!_activeStreams.TryRemove(new KeyValuePair<string, ActiveStreamState>(sequenceId, state)))
            {
                return HandledTask;
            }

            Interlocked.Decrement(ref _streamCount);

            if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var ftnRaw))
            {
                _logger.LogWarning("Completed stream {SequenceId} missing FullTypeName header", sequenceId);
                return HandledTask;
            }

            var fullTypeName = HeaderDecoder.Decode(ftnRaw);
            if (!_typeRegistry.TryResolve(fullTypeName!, out var resolvedType))
            {
                _logger.LogWarning("Unregistered type '{TypeName}' for completed stream. Rejecting", fullTypeName);
                return HandledTask;
            }

            if (!_streamHandlerRegistry.TryGet(resolvedType, out var descriptor))
            {
                _logger.LogWarning("No IStreamHandler registered for {MessageType}", resolvedType.FullName);
                return HandledTask;
            }

            var handler = _scopeAccessor.Current.GetService(descriptor.HandlerInterfaceType);
            if (handler == null)
            {
                _logger.LogWarning("No IStreamHandler registered for {MessageType}", resolvedType.FullName);
                return HandledTask;
            }

            // The serializer's ReadOnlySequence overload reads across segments via
            // Utf8JsonReader without flattening — keeps the streaming path zero-copy.
            var assembledSequence = state.Stream.ReadSequence();
            var originalMessage = _serializer.Deserialize(in assembledSequence, resolvedType);

            return InvokeHandlerAsync(descriptor, handler, originalMessage!, state.Stream, sequenceId, cancellationToken);
        }

        return HandledTask;
    }

    private void EvictStaleStreams()
    {
        var cutoff = _timeProvider.GetUtcNow() - StreamTimeout;
        foreach (var kvp in _activeStreams)
        {
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Stream handler {HandlerType} threw an unhandled exception for stream {SequenceId}",
                handler.GetType().FullName, sequenceId);
            throw;
        }
        return ProcessResult.Handled;
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
    private sealed record ActiveStreamState(MessageBusReadStream Stream, DateTimeOffset LastSeenUtc);
}
