using System.Text;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public class MessageDispatcher
{
    private readonly IMessageSerializer _serializer;
    private readonly IFilterPipeline _filterPipeline;
    private readonly IList<IMessageProcessor> _processors;
    private readonly ILogger<MessageDispatcher> _logger;

    public MessageDispatcher(
        IMessageSerializer serializer,
        IFilterPipeline filterPipeline,
        IList<IMessageProcessor> processors,
        ILogger<MessageDispatcher> logger)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _filterPipeline = filterPipeline ?? throw new ArgumentNullException(nameof(filterPipeline));
        _processors = processors ?? throw new ArgumentNullException(nameof(processors));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ConsumeEventResult> Dispatch(byte[] messageBytes, string messageType, IDictionary<string, object> headers)
    {
        try
        {
            // 1. Resolve CLR Type from FullTypeName header
            if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var fullTypeNameRaw))
                throw new InvalidOperationException("Message is missing FullTypeName header.");

            var fullTypeName = fullTypeNameRaw is byte[] bytes
                ? Encoding.UTF8.GetString(bytes)
                : fullTypeNameRaw?.ToString() ?? throw new InvalidOperationException("FullTypeName header is null.");

            var type = Type.GetType(fullTypeName)
                ?? throw new InvalidOperationException($"Cannot resolve type '{fullTypeName}'.");

            // 2. Build envelope
            var envelope = new Envelope { Headers = headers, Body = messageBytes };

            // 3. Run ReplyProcessor first (before deserialization) — first in chain
            if (_processors.Count > 0)
            {
                var replyResult = await _processors[0].ProcessAsync(messageBytes, type, null, headers, envelope);
                if (replyResult == ProcessResult.Handled)
                    return new ConsumeEventResult { Success = true };
            }

            // 4. Deserialize the message
            var message = _serializer.Deserialize(messageBytes, type);

            // 5. Run BeforeConsumingFilters
            bool blocked = _filterPipeline.ExecuteBeforeConsumingFilters(envelope);
            if (blocked)
                return new ConsumeEventResult { Success = true };

            // 6. Iterate remaining processors
            for (int i = 1; i < _processors.Count; i++)
            {
                var result = await _processors[i].ProcessAsync(messageBytes, type, message, headers, envelope);
                if (result == ProcessResult.Handled)
                {
                    _filterPipeline.ExecuteAfterConsumingFilters(envelope);
                    return new ConsumeEventResult { Success = true };
                }
            }

            // No processor handled the message
            _logger.LogWarning("No processor handled message of type {MessageType}", type.FullName);
            _filterPipeline.ExecuteAfterConsumingFilters(envelope);

            return new ConsumeEventResult { Success = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching message of type {MessageType}", messageType);
            return new ConsumeEventResult { Success = false, Exception = ex };
        }
    }
}
