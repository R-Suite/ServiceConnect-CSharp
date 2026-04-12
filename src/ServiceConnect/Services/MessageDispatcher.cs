using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Services;

public sealed class MessageDispatcher(
    IMessageSerializer serializer,
    IFilterPipeline filterPipeline,
    IList<IMessageProcessor> processors,
    ILogger<MessageDispatcher> logger,
    IBusConfiguration config,
    IPipelineConfiguration pipelineConfig,
    IServiceProvider serviceProvider,
    IMessageTypeRegistry typeRegistry) : IMessageDispatcher
{
    private readonly IMessageSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    private readonly IFilterPipeline _filterPipeline = filterPipeline ?? throw new ArgumentNullException(nameof(filterPipeline));
    private readonly IList<IMessageProcessor> _processors = processors ?? throw new ArgumentNullException(nameof(processors));
    private readonly ILogger<MessageDispatcher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IBusConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly IPipelineConfiguration _pipelineConfig = pipelineConfig ?? throw new ArgumentNullException(nameof(pipelineConfig));
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly IMessageTypeRegistry _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));

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

            if (!_typeRegistry.TryResolve(fullTypeName, out var type))
            {
                _logger.LogWarning("Unregistered message type '{TypeName}'. Rejecting", fullTypeName);
                return new ConsumeEventResult { Success = false };
            }

            // 2. Build envelope
            var envelope = new Envelope { Headers = headers, Body = messageBytes };

            // 3. Run pre-deserialization processors (e.g., ReplyProcessor, StreamProcessor)
            //    These processors can handle messages without the deserialized body.
            foreach (var proc in _processors)
            {
                if (!proc.RunBeforeDeserialization) continue;
                var preResult = await proc.ProcessAsync(messageBytes, type, null, headers, envelope);
                if (preResult == ProcessResult.Handled)
                    return new ConsumeEventResult { Success = true };
            }

            // 4. Deserialize the message
            var message = _serializer.Deserialize(messageBytes, type);

            // 5. Run BeforeConsumingFilters
            bool blocked = _filterPipeline.ExecuteBeforeConsumingFilters(envelope);
            if (blocked)
                return new ConsumeEventResult { Success = true };

            // 6. Run post-deserialization processors, wrapped in processing middleware
            async Task<ConsumeEventResult> RunProcessors(byte[] mb, Type mt, object m, IDictionary<string, object> h, Envelope e)
            {
                foreach (var proc in _processors)
                {
                    if (proc.RunBeforeDeserialization) continue;
                    var result = await proc.ProcessAsync(mb, mt, m, h, e);
                    if (result == ProcessResult.Handled)
                    {
                        _filterPipeline.ExecuteAfterConsumingFilters(e);
                        return new ConsumeEventResult { Success = true };
                    }
                }

                _logger.LogWarning("No processor handled message of type {MessageType}", mt.FullName);
                _filterPipeline.ExecuteAfterConsumingFilters(e);
                return new ConsumeEventResult { Success = true };
            }

            var middlewareTypes = _pipelineConfig.MessageProcessingMiddleware;
            if (middlewareTypes.Count == 0)
                return await RunProcessors(messageBytes, type, message, headers, envelope);

            MessageProcessingDelegate chain = (mb, mt, m, h, e) => RunProcessors(mb, mt, m, h, e);
            for (int i = middlewareTypes.Count - 1; i >= 0; i--)
            {
                var mw = (IMessageProcessingMiddleware)_serviceProvider.GetRequiredService(middlewareTypes[i]);
                var next = chain;
                chain = (mb, mt, m, h, e) => mw.Process(mb, mt, m, h, e, next);
            }
            return await chain(messageBytes, type, message, headers, envelope);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching message of type {MessageType}", messageType);
            try
            {
                _config.ExceptionHandler?.Invoke(ex);
            }
            catch (Exception handlerEx)
            {
                _logger.LogWarning(handlerEx, "ExceptionHandler threw while handling dispatch error");
            }
            return new ConsumeEventResult { Success = false, Exception = ex };
        }
    }
}
