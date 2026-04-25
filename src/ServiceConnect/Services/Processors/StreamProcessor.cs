using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class StreamProcessor : IMessageProcessor, IAsyncDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StreamProcessor> _logger;
    private readonly IMessageTypeRegistry _typeRegistry;
    private readonly StreamHandlerRegistry _streamHandlerRegistry;
    private readonly IMessageSerializer _serializer;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, ActiveStreamState> _activeStreams = new();
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
        IServiceProvider serviceProvider,
        ILogger<StreamProcessor> logger,
        IMessageTypeRegistry typeRegistry,
        StreamHandlerRegistry streamHandlerRegistry,
        IMessageSerializer serializer,
        TimeProvider timeProvider)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        _streamHandlerRegistry = streamHandlerRegistry ?? throw new ArgumentNullException(nameof(streamHandlerRegistry));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _cleanupTimer = _timeProvider.CreateTimer(_ => EvictStaleStreams(), null, StreamCleanupInterval, StreamCleanupInterval);
    }

    public bool RunBeforeDeserialization => true;

    public Task<ProcessResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!headers.TryGetValue(HeaderKeys.MessageType, out var msgTypeRaw))
            return NotHandledTask;

        var msgType = HeaderDecoder.Decode(msgTypeRaw);
        if (msgType != HeaderKeys.ByteStream)
            return NotHandledTask;

        if (!headers.TryGetValue(HeaderKeys.SequenceId, out var seqIdRaw))
            return NotHandledTask;
        var sequenceId = HeaderDecoder.Decode(seqIdRaw)!;

        // SequenceId must be a valid GUID to prevent arbitrary-string abuse.
        if (!Guid.TryParse(sequenceId, out _))
        {
            _logger.LogWarning("Stream packet has non-GUID SequenceId '{Value}'; discarding", sequenceId);
            return NotHandledTask;
        }

        // Reject new streams when the active-stream limit is reached.
        if (!_activeStreams.ContainsKey(sequenceId) && _activeStreams.Count >= MaxActiveStreams)
        {
            _logger.LogWarning("Active stream limit ({Limit}) reached; rejecting new stream {SequenceId}", MaxActiveStreams, sequenceId);
            return NotHandledTask;
        }

        if (!headers.TryGetValue(HeaderKeys.PacketNumber, out var pnRaw))
            return NotHandledTask;
        var pnString = HeaderDecoder.Decode(pnRaw);
        if (!long.TryParse(pnString, out var packetNumber))
        {
            _logger.LogWarning("Stream packet has invalid PacketNumber header '{Value}'; discarding", pnString);
            return HandledTask; // Handled to prevent infinite requeue
        }

        var state = _activeStreams.GetOrAdd(sequenceId, id => new ActiveStreamState(new MessageBusReadStream(id), _timeProvider.GetUtcNow()));

        try
        {
            state.Stream.Write(messageBytes.ToArray(), packetNumber);

            // Touch: replace the dict entry with a new ActiveStreamState carrying a fresh
            // LastSeenUtc. The eviction sweep's TryRemove(KVP) compares records by
            // structural equality; mutating LastSeenUtc in place would leave the record
            // structurally equal and defeat that check, which is why we replace the entry
            // instead. The CAS loop retries on contention with another touch / dispatch path.
            ActiveStreamState refreshed;
            while (true)
            {
                if (!_activeStreams.TryGetValue(sequenceId, out var current))
                {
                    // Eviction or completion-dispatch removed the entry between our
                    // GetOrAdd and now. Treat as an idempotent ack.
                    return HandledTask;
                }
                refreshed = current with { LastSeenUtc = _timeProvider.GetUtcNow() };
                if (_activeStreams.TryUpdate(sequenceId, refreshed, current))
                {
                    state = refreshed;
                    break;
                }
            }

            if (headers.TryGetValue(HeaderKeys.LastPacketNumber, out var lpnRaw))
            {
                var lpnString = HeaderDecoder.Decode(lpnRaw);
                if (!long.TryParse(lpnString, out var lastPacketNumber))
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
            _activeStreams.TryRemove(sequenceId, out _);
            return HandledTask;
        }

        if (state.Stream.IsComplete())
        {
            // Race: two final-packet deliveries can both observe IsComplete() == true.
            // Only the caller that wins TryRemove transitions the dict entry from
            // "present" to "removed"; the loser sees a stale state and must idempotent-ack.
            if (!_activeStreams.TryRemove(new KeyValuePair<string, ActiveStreamState>(sequenceId, state)))
                return HandledTask;

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

            var handler = _serviceProvider.GetService(descriptor.HandlerInterfaceType);
            if (handler == null)
            {
                _logger.LogWarning("No IStreamHandler registered for {MessageType}", resolvedType.FullName);
                return HandledTask;
            }

            descriptor.SetStream(handler, state.Stream);

            var assembledSequence = state.Stream.ReadSequence();
            var originalMessage = _serializer.Deserialize(in assembledSequence, resolvedType);

            return InvokeHandlerAsync(descriptor, handler, originalMessage!, sequenceId, cancellationToken);
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
                    _logger.LogWarning("Evicted incomplete stream {SequenceId} after timeout", kvp.Key);
            }
        }
    }

    private async Task<ProcessResult> InvokeHandlerAsync(
        StreamHandlerDescriptor descriptor,
        object handler,
        object originalMessage,
        string sequenceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await descriptor.InvokeExecuteAsync(handler, originalMessage, cancellationToken).ConfigureAwait(false);
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

    public ValueTask DisposeAsync()
    {
        return _cleanupTimer.DisposeAsync();
    }

    // Immutable so updates require a new instance via ConcurrentDictionary.TryUpdate;
    // the eviction sweep's KVP-based TryRemove compares records by structural equality,
    // so a concurrent touch produces an unequal record and the sweep no-ops on the stale
    // value (in-place mutation would stay structurally equal and defeat the check).
    private sealed record ActiveStreamState(MessageBusReadStream Stream, DateTimeOffset LastSeenUtc);
}
