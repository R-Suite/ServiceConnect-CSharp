using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class StreamProcessor : IMessageProcessor, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StreamProcessor> _logger;
    private readonly IMessageTypeRegistry _typeRegistry;
    private readonly StreamHandlerRegistry _streamHandlerRegistry;
    private readonly ConcurrentDictionary<string, MessageBusReadStream> _activeStreams = new();
    private readonly ConcurrentDictionary<string, DateTime> _streamTimestamps = new();
    private readonly Timer _cleanupTimer;
    /// <summary>
    /// Maximum time a partial stream may sit without new packets before it is evicted.
    /// Tuned to balance memory held by stale streams against transient network stalls.
    /// </summary>
    private static readonly TimeSpan StreamTimeout = TimeSpan.FromMinutes(5);
    /// <summary>Interval at which the sweeper runs to evict stale partial streams.</summary>
    private static readonly TimeSpan StreamCleanupInterval = TimeSpan.FromMinutes(1);

    public StreamProcessor(
        IServiceProvider serviceProvider,
        ILogger<StreamProcessor> logger,
        IMessageTypeRegistry typeRegistry,
        StreamHandlerRegistry streamHandlerRegistry)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        _streamHandlerRegistry = streamHandlerRegistry ?? throw new ArgumentNullException(nameof(streamHandlerRegistry));
        _cleanupTimer = new Timer(_ => EvictStaleStreams(), null, StreamCleanupInterval, StreamCleanupInterval);
    }

    public bool RunBeforeDeserialization => true;

    public Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!headers.TryGetValue(HeaderKeys.MessageType, out var msgTypeRaw))
            return Task.FromResult(ProcessResult.NotHandled);

        var msgType = HeaderDecoder.Decode(msgTypeRaw);
        if (msgType != HeaderKeys.ByteStream)
            return Task.FromResult(ProcessResult.NotHandled);

        if (!headers.TryGetValue(HeaderKeys.SequenceId, out var seqIdRaw))
            return Task.FromResult(ProcessResult.NotHandled);
        var sequenceId = HeaderDecoder.Decode(seqIdRaw)!;

        if (!headers.TryGetValue(HeaderKeys.PacketNumber, out var pnRaw))
            return Task.FromResult(ProcessResult.NotHandled);
        var pnString = HeaderDecoder.Decode(pnRaw);
        if (!long.TryParse(pnString, out var packetNumber))
        {
            _logger.LogWarning("Stream packet has invalid PacketNumber header '{Value}'; discarding", pnString);
            return Task.FromResult(ProcessResult.Handled); // Handled to prevent infinite requeue
        }

        var stream = _activeStreams.GetOrAdd(sequenceId, id => new MessageBusReadStream(id));

        stream.Write(messageBytes, packetNumber);
        _streamTimestamps[sequenceId] = DateTime.UtcNow;

        if (headers.TryGetValue(HeaderKeys.LastPacketNumber, out var lpnRaw))
        {
            var lpnString = HeaderDecoder.Decode(lpnRaw);
            if (!long.TryParse(lpnString, out var lastPacketNumber))
            {
                _logger.LogWarning("Stream packet has invalid LastPacketNumber header '{Value}'; discarding", lpnString);
                return Task.FromResult(ProcessResult.Handled);
            }
            stream.SetLastPacketNumber(lastPacketNumber);
        }

        if (stream.IsComplete())
        {
            _activeStreams.TryRemove(sequenceId, out _);
            _streamTimestamps.TryRemove(sequenceId, out _);

            if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var ftnRaw))
            {
                _logger.LogWarning("Completed stream {SequenceId} missing FullTypeName header", sequenceId);
                return Task.FromResult(ProcessResult.Handled);
            }

            var fullTypeName = HeaderDecoder.Decode(ftnRaw);
            if (!_typeRegistry.TryResolve(fullTypeName!, out var resolvedType))
            {
                _logger.LogWarning("Unregistered type '{TypeName}' for completed stream. Rejecting", fullTypeName);
                return Task.FromResult(ProcessResult.Handled);
            }

            if (!_streamHandlerRegistry.TryGet(resolvedType, out var descriptor))
            {
                _logger.LogWarning("No IStreamHandler registered for {MessageType}", resolvedType.FullName);
                return Task.FromResult(ProcessResult.Handled);
            }

            var handler = _serviceProvider.GetService(descriptor.HandlerInterfaceType);
            if (handler == null)
            {
                _logger.LogWarning("No IStreamHandler registered for {MessageType}", resolvedType.FullName);
                return Task.FromResult(ProcessResult.Handled);
            }

            descriptor.SetStream(handler, stream);

            var serializer = _serviceProvider.GetRequiredService<IMessageSerializer>();
            var assembledBytes = stream.Read();
            var originalMessage = serializer.Deserialize(assembledBytes, resolvedType);

            descriptor.InvokeExecute(handler, originalMessage!);
        }

        return Task.FromResult(ProcessResult.Handled);
    }

    private void EvictStaleStreams()
    {
        var cutoff = DateTime.UtcNow - StreamTimeout;
        foreach (var kvp in _streamTimestamps)
        {
            if (kvp.Value < cutoff)
            {
                _activeStreams.TryRemove(kvp.Key, out _);
                _streamTimestamps.TryRemove(kvp.Key, out _);
                _logger.LogWarning("Evicted incomplete stream {SequenceId} after timeout", kvp.Key);
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }
}
