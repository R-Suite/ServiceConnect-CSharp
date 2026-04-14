using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Services;

public sealed class MessageDispatcher : IMessageDispatcher
{
    private readonly IMessageSerializer _serializer;
    private readonly IFilterPipeline _filterPipeline;
    private readonly IList<IMessageProcessor> _processors;
    private readonly ILogger<MessageDispatcher> _logger;
    private readonly IBusConfiguration _config;
    private readonly IPipelineConfiguration _pipelineConfig;
    private readonly IServiceProvider _serviceProvider;
    private readonly IMessageTypeRegistry _typeRegistry;

    // Chain is built once (lazily) at first message dispatch and cached.
    // IMessageProcessingMiddleware implementations MUST be singletons; scoped/transient
    // registrations will be silently promoted to singleton lifetime here (M-3).
    private readonly Lazy<MessageProcessingDelegate> _processingChain;

    public MessageDispatcher(
        IMessageSerializer serializer,
        IFilterPipeline filterPipeline,
        IList<IMessageProcessor> processors,
        ILogger<MessageDispatcher> logger,
        IBusConfiguration config,
        IPipelineConfiguration pipelineConfig,
        IServiceProvider serviceProvider,
        IMessageTypeRegistry typeRegistry)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _filterPipeline = filterPipeline ?? throw new ArgumentNullException(nameof(filterPipeline));
        _processors = processors ?? throw new ArgumentNullException(nameof(processors));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _pipelineConfig = pipelineConfig ?? throw new ArgumentNullException(nameof(pipelineConfig));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        _processingChain = new Lazy<MessageProcessingDelegate>(BuildProcessingChain, isThreadSafe: true);
    }

    public async Task<ConsumeEventResult> Dispatch(ReadOnlyMemory<byte> messageBytes, string messageType, IDictionary<string, object> headers, CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. Extract FullTypeName header
            if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var fullTypeNameRaw))
                throw new InvalidOperationException("Message is missing FullTypeName header.");

            var fullTypeName = HeaderDecoder.Decode(fullTypeNameRaw) ?? throw new InvalidOperationException("FullTypeName header is null.");

            // 2. Build envelope
            var envelope = new Envelope { Headers = headers, Body = messageBytes };

            // 3. Run pre-deserialization processors (ReplyProcessor, StreamProcessor).
            //    These operate on headers only and don't need the resolved CLR type.
            //    ReplyProcessor uses the expected type stored at request time, not the wire type.
            //    This runs before the registry check so reply messages for unregistered
            //    types (e.g., requester with ScanForMessageHandlers=false) are handled.
            foreach (var proc in _processors)
            {
                if (!proc.RunBeforeDeserialization) continue;
                var preResult = await proc.ProcessAsync(messageBytes, typeof(Message), null, headers, envelope, cancellationToken);
                if (preResult == ProcessResult.Handled)
                    return new ConsumeEventResult { Success = true };
            }

            // 4. Resolve CLR Type from registry (strict: no Type.GetType fallback).
            if (!_typeRegistry.TryResolve(fullTypeName, out var type))
            {
                _logger.LogWarning("Unregistered message type '{TypeName}'. Rejecting", fullTypeName);
                return new ConsumeEventResult { Success = false };
            }

            // 5. Deserialize the message — .ToArray() at the serializer boundary (P-003).
            //    When P-040 adds span-based overloads, this allocation goes away.
            var message = _serializer.Deserialize(messageBytes.ToArray(), type);

            // 6. Run BeforeConsumingFilters
            bool blocked = await _filterPipeline.ExecuteBeforeConsumingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (blocked)
                return new ConsumeEventResult { Success = true };

            // 7. Run post-deserialization processors wrapped in the cached processing chain
            return await _processingChain.Value(messageBytes, type, message, headers, envelope, cancellationToken);
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

    private async Task<ConsumeEventResult> RunProcessors(ReadOnlyMemory<byte> mb, Type mt, object m, IDictionary<string, object> h, Envelope e, CancellationToken ct)
    {
        foreach (var proc in _processors)
        {
            if (proc.RunBeforeDeserialization) continue;
            var result = await proc.ProcessAsync(mb, mt, m, h, e, ct);
            if (result == ProcessResult.Handled)
            {
                await _filterPipeline.ExecuteAfterConsumingFiltersAsync(e, ct).ConfigureAwait(false);
                return new ConsumeEventResult { Success = true };
            }
        }

        _logger.LogWarning("No processor handled message of type {MessageType}", mt.FullName);
        await _filterPipeline.ExecuteAfterConsumingFiltersAsync(e, ct).ConfigureAwait(false);
        return new ConsumeEventResult { Success = true };
    }

    private MessageProcessingDelegate BuildProcessingChain()
    {
        var middlewareTypes = _pipelineConfig.MessageProcessingMiddleware;
        if (middlewareTypes.Count == 0)
            return RunProcessors;

        MessageProcessingDelegate chain = RunProcessors;
        for (int i = middlewareTypes.Count - 1; i >= 0; i--)
        {
            var mw = (IMessageProcessingMiddleware)_serviceProvider.GetRequiredService(middlewareTypes[i]);
            var next = chain;
            chain = (mb, mt, m, h, e, ct) => mw.Process(mb, mt, m, h, e, next, ct);
        }
        return chain;
    }
}
