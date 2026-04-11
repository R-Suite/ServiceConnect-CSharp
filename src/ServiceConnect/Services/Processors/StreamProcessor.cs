using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public class StreamProcessor : IMessageProcessor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StreamProcessor> _logger;
    private readonly ConcurrentDictionary<string, MessageBusReadStream> _activeStreams = new();

    public StreamProcessor(IServiceProvider serviceProvider, ILogger<StreamProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public bool RunBeforeDeserialization => true;

    public async Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope)
    {
        if (!headers.TryGetValue(HeaderKeys.MessageType, out var msgTypeRaw))
            return ProcessResult.NotHandled;

        var msgType = msgTypeRaw is byte[] mtBytes ? Encoding.UTF8.GetString(mtBytes) : msgTypeRaw?.ToString();
        if (msgType != "ByteStream")
            return ProcessResult.NotHandled;

        if (!headers.TryGetValue(HeaderKeys.SequenceId, out var seqIdRaw))
            return ProcessResult.NotHandled;
        var sequenceId = seqIdRaw is byte[] siBytes ? Encoding.UTF8.GetString(siBytes) : seqIdRaw?.ToString()!;

        if (!headers.TryGetValue(HeaderKeys.PacketNumber, out var pnRaw))
            return ProcessResult.NotHandled;
        var packetNumber = long.Parse(pnRaw is byte[] pnBytes ? Encoding.UTF8.GetString(pnBytes) : pnRaw?.ToString()!);

        var stream = _activeStreams.GetOrAdd(sequenceId, _ => new MessageBusReadStream { SequenceId = sequenceId });

        stream.Write(messageBytes, packetNumber);

        if (headers.TryGetValue(HeaderKeys.LastPacketNumber, out var lpnRaw))
        {
            var lastPacketNumber = long.Parse(lpnRaw is byte[] lpnBytes ? Encoding.UTF8.GetString(lpnBytes) : lpnRaw?.ToString()!);
            stream.LastPacketNumber = lastPacketNumber;
        }

        if (stream.IsComplete())
        {
            _activeStreams.TryRemove(sequenceId, out _);

            if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var ftnRaw))
            {
                _logger.LogWarning("Completed stream {SequenceId} missing FullTypeName header", sequenceId);
                return ProcessResult.Handled;
            }

            var fullTypeName = ftnRaw is byte[] ftnBytes ? Encoding.UTF8.GetString(ftnBytes) : ftnRaw?.ToString();
            var resolvedType = Type.GetType(fullTypeName!);
            if (resolvedType == null)
            {
                _logger.LogWarning("Cannot resolve type {TypeName} for completed stream", fullTypeName);
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
            executeMethod?.Invoke(handler, new[] { originalMessage });
        }

        return ProcessResult.Handled;
    }
}
