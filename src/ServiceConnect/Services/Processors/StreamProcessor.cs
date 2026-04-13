using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public sealed class StreamProcessor : IMessageProcessor, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StreamProcessor> _logger;
    private readonly IMessageTypeRegistry _typeRegistry;
    private readonly ConcurrentDictionary<string, MessageBusReadStream> _activeStreams = new();
    private readonly ConcurrentDictionary<string, DateTime> _streamTimestamps = new();
    private readonly Timer _cleanupTimer;
    private static readonly TimeSpan StreamTimeout = TimeSpan.FromMinutes(5);

    public StreamProcessor(IServiceProvider serviceProvider, ILogger<StreamProcessor> logger, IMessageTypeRegistry typeRegistry)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        _cleanupTimer = new Timer(_ => EvictStaleStreams(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public bool RunBeforeDeserialization => true;

    public async Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!headers.TryGetValue(HeaderKeys.MessageType, out var msgTypeRaw))
            return ProcessResult.NotHandled;

        var msgType = HeaderDecoder.Decode(msgTypeRaw);
        if (msgType != HeaderKeys.ByteStream)
            return ProcessResult.NotHandled;

        if (!headers.TryGetValue(HeaderKeys.SequenceId, out var seqIdRaw))
            return ProcessResult.NotHandled;
        var sequenceId = HeaderDecoder.Decode(seqIdRaw)!;

        if (!headers.TryGetValue(HeaderKeys.PacketNumber, out var pnRaw))
            return ProcessResult.NotHandled;
        var pnString = HeaderDecoder.Decode(pnRaw);
        if (!long.TryParse(pnString, out var packetNumber))
        {
            _logger.LogWarning("Stream packet has invalid PacketNumber header '{Value}'; discarding", pnString);
            return ProcessResult.Handled; // Handled to prevent infinite requeue
        }

        var stream = _activeStreams.GetOrAdd(sequenceId, _ => new MessageBusReadStream { SequenceId = sequenceId });

        stream.Write(messageBytes, packetNumber);
        _streamTimestamps[sequenceId] = DateTime.UtcNow;

        if (headers.TryGetValue(HeaderKeys.LastPacketNumber, out var lpnRaw))
        {
            var lpnString = HeaderDecoder.Decode(lpnRaw);
            if (!long.TryParse(lpnString, out var lastPacketNumber))
            {
                _logger.LogWarning("Stream packet has invalid LastPacketNumber header '{Value}'; discarding", lpnString);
                return ProcessResult.Handled;
            }
            stream.LastPacketNumber = lastPacketNumber;
        }

        if (stream.IsComplete())
        {
            _activeStreams.TryRemove(sequenceId, out _);
            _streamTimestamps.TryRemove(sequenceId, out _);

            if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var ftnRaw))
            {
                _logger.LogWarning("Completed stream {SequenceId} missing FullTypeName header", sequenceId);
                return ProcessResult.Handled;
            }

            var fullTypeName = HeaderDecoder.Decode(ftnRaw);
            if (!_typeRegistry.TryResolve(fullTypeName!, out var resolvedType))
            {
                _logger.LogWarning("Unregistered type '{TypeName}' for completed stream. Rejecting", fullTypeName);
                return ProcessResult.Handled;
            }

            var handlerType = typeof(IStreamHandler<>).MakeGenericType(resolvedType);
            var handler = _serviceProvider.GetService(handlerType);
            if (handler == null)
            {
                _logger.LogWarning("No IStreamHandler registered for {MessageType}", resolvedType.FullName);
                return ProcessResult.Handled;
            }

            var streamProp = handlerType.GetProperty("Stream");
            streamProp?.SetValue(handler, stream);

            var serializer = _serviceProvider.GetRequiredService<IMessageSerializer>();
            var assembledBytes = stream.Read();
            var originalMessage = serializer.Deserialize(assembledBytes, resolvedType);

            var executeMethod = handlerType.GetMethod("Execute");
            var executeResult = executeMethod?.Invoke(handler, new[] { originalMessage });
            if (executeResult is Task executeTask)
                await executeTask.ConfigureAwait(false);
        }

        return ProcessResult.Handled;
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
