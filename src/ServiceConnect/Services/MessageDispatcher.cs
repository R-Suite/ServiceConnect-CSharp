using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services.Processors;

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
    // registrations will be silently promoted to singleton lifetime here.
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
        Envelope? envelope = null;
        var beforeFiltersRan = false;
        try
        {
            // 1. Extract FullTypeName header
            if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var fullTypeNameRaw))
                throw new InvalidOperationException("Message is missing FullTypeName header.");

            var fullTypeName = HeaderDecoder.Decode(fullTypeNameRaw) ?? throw new InvalidOperationException("FullTypeName header is null.");

            // 2. Build envelope
            envelope = new Envelope { Headers = headers, Body = messageBytes };

            ReplyProcessor? replyProcessor = null;
            var hasResponseMessageId = headers.ContainsKey(HeaderKeys.ResponseMessageId);

            // 3. Run stream-style pre-deserialization processors before type resolution.
            foreach (var proc in _processors)
            {
                if (proc is ReplyProcessor typedReplyProcessor)
                {
                    replyProcessor = typedReplyProcessor;
                    continue;
                }

                if (!proc.RunBeforeDeserialization) continue;
                var preResult = await proc.ProcessAsync(messageBytes, typeof(Message), null, headers, envelope, cancellationToken);
                if (preResult == ProcessResult.Handled)
                    return new ConsumeEventResult { Success = true };
            }

            // 4. Resolve CLR type for handler dispatch. Reply traffic only needs a best-effort
            // wire type because RequestReplyManager owns the actual reply deserialization.
            var typeResolvedFromRegistry = _typeRegistry.TryResolve(fullTypeName, out var type);
            if (!typeResolvedFromRegistry)
            {
                if (!hasResponseMessageId)
                {
                    _logger.LogWarning("Unregistered message type '{TypeName}'. Rejecting", fullTypeName);
                    return new ConsumeEventResult { Success = false };
                }

                type = Type.GetType(fullTypeName, throwOnError: false) ?? typeof(Message);
            }

            // 5. Run BeforeConsumingFilters. Once this returns, AfterConsumingFilters must run
            // on every exit path — enforced by the finally below.
            bool blocked = await _filterPipeline.ExecuteBeforeConsumingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false);
            beforeFiltersRan = true;
            if (blocked)
                return new ConsumeEventResult { Success = true };

            if (replyProcessor != null && hasResponseMessageId)
            {
                var replyResult = await replyProcessor.ProcessAsync(messageBytes, type, null, headers, envelope, cancellationToken).ConfigureAwait(false);
                if (replyResult == ProcessResult.Handled)
                    return new ConsumeEventResult { Success = true };

                return new ConsumeEventResult
                {
                    Success = false,
                    Exception = new InvalidOperationException("Reply message did not match a pending request.")
                };
            }

            if (!typeResolvedFromRegistry)
                return new ConsumeEventResult { Success = false };

            var message = _serializer.Deserialize(messageBytes, type);

            // 6. Run post-deserialization processors wrapped in the cached processing chain
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
        finally
        {
            if (beforeFiltersRan && envelope != null)
            {
                try
                {
                    await _filterPipeline.ExecuteAfterConsumingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception afterEx)
                {
                    _logger.LogWarning(afterEx, "AfterConsumingFilters threw while finalising dispatch of {MessageType}", messageType);
                }
            }
        }
    }

    private async Task<ConsumeEventResult> RunProcessors(ReadOnlyMemory<byte> mb, Type mt, object m, IDictionary<string, object> h, Envelope e, CancellationToken ct)
    {
        foreach (var proc in _processors)
        {
            if (proc.RunBeforeDeserialization) continue;
            var result = await proc.ProcessAsync(mb, mt, m, h, e, ct);
            if (result == ProcessResult.Handled)
                return new ConsumeEventResult { Success = true };
        }

        _logger.LogWarning("No processor handled message of type {MessageType}", mt.FullName);
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
