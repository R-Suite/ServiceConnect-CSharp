using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services.Processors;

namespace ServiceConnect.Services;

/// <summary>
/// Deserializes incoming envelopes and routes them through filters, processors, and middleware.
/// A fresh DI scope is created for each dispatch and flowed through <see cref="ConsumeScopeAccessor"/>
/// so filters, middleware, and handlers share the same per-message container scope.
/// <para>
/// Filters (before- and after-consuming) run on every dispatch, including pre-deserialization
/// processors such as <see cref="StreamProcessor"/> and <see cref="ReplyProcessor"/>.
/// <see cref="IMessageProcessingMiddleware"/> only wraps the post-deserialization dispatch
/// path because its delegate signature requires a non-null <c>object message</c>; pre-deserialization
/// processors handle raw bytes without a resolved message instance and therefore bypass middleware by design.
/// </para>
/// </summary>
/// <remarks>
/// Creates a dispatcher for incoming broker messages.
/// </remarks>
public sealed class MessageDispatcher(
    IMessageSerializer serializer,
    IFilterPipeline filterPipeline,
    IList<IMessageProcessor> processors,
    ILogger<MessageDispatcher> logger,
    IBusConfiguration config,
    IPipelineConfiguration pipelineConfig,
    IServiceScopeFactory scopeFactory,
    ConsumeScopeAccessor scopeAccessor,
    IMessageTypeRegistry typeRegistry) : IMessageDispatcher
{
    private readonly IMessageSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    private readonly IFilterPipeline _filterPipeline = filterPipeline ?? throw new ArgumentNullException(nameof(filterPipeline));
    private readonly IList<IMessageProcessor> _processors = processors ?? throw new ArgumentNullException(nameof(processors));
    private readonly ILogger<MessageDispatcher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IBusConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly IPipelineConfiguration _pipelineConfig = pipelineConfig ?? throw new ArgumentNullException(nameof(pipelineConfig));
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly ConsumeScopeAccessor _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));
    private readonly IMessageTypeRegistry _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));

    /// <inheritdoc />
    public async Task<ConsumeEventResult> Dispatch(ReadOnlyMemory<byte> messageBytes, string messageType, IDictionary<string, object> headers, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        using var _ = _scopeAccessor.Push(scope.ServiceProvider);

        Envelope? envelope = null;
        var beforeFiltersRan = false;
        try
        {
            // 1. Collect candidate wire-type names, in priority order. The public
            //    IMessageDispatcher contract passes messageType as a first-class parameter,
            //    so it must be honoured — but a transport may pass a short/alias name that
            //    doesn't resolve in the registry while also stamping the AQN header. Try
            //    each candidate against the registry until one resolves.
            string? fullTypeName = null;
            string? primaryCandidate = string.IsNullOrWhiteSpace(messageType) ? null : messageType;
            string? fullTypeNameCandidate = headers.TryGetValue(HeaderKeys.FullTypeName, out var fullTypeNameRaw)
                ? HeaderDecoder.Decode(fullTypeNameRaw) : null;
            string? typeNameCandidate = headers.TryGetValue(HeaderKeys.TypeName, out var typeNameRaw)
                ? HeaderDecoder.Decode(typeNameRaw) : null;

            if (primaryCandidate is null && fullTypeNameCandidate is null && typeNameCandidate is null)
            {
                throw new InvalidOperationException("Message is missing type information: messageType parameter is empty and neither FullTypeName nor TypeName header is present.");
            }

            fullTypeName = primaryCandidate ?? fullTypeNameCandidate ?? typeNameCandidate!;

            envelope = new Envelope { Headers = headers, Body = messageBytes };

            var hasResponseMessageId = headers.ContainsKey(HeaderKeys.ResponseMessageId);

            // Before-consuming filters run first so they gate every dispatch path —
            // including pre-deserialization processors like StreamProcessor. Running
            // filters here guarantees stream packets and replies traverse the same
            // pre- and post-consume filter stages as any other message
            // (after-filters only fire once beforeFiltersRan is set).
            FilterAction beforeAction = await _filterPipeline.ExecuteBeforeConsumingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false);
            beforeFiltersRan = true;
            if (beforeAction == FilterAction.Stop)
            {
                return new ConsumeEventResult { Success = true };
            }

            ReplyProcessor? replyProcessor = null;

            foreach (var proc in _processors)
            {
                if (proc is ReplyProcessor typedReplyProcessor)
                {
                    replyProcessor = typedReplyProcessor;
                    continue;
                }

                if (!proc.RunBeforeDeserialization)
                {
                    continue;
                }

                var preResult = await proc.ProcessAsync(messageBytes, typeof(Message), null, headers, envelope, cancellationToken).ConfigureAwait(false);
                if (preResult == ProcessResult.Handled)
                {
                    return new ConsumeEventResult { Success = true };
                }
            }

            Type? type = null;
            bool typeResolvedFromRegistry =
                (primaryCandidate is not null && _typeRegistry.TryResolve(primaryCandidate, out type))
                || (fullTypeNameCandidate is not null && _typeRegistry.TryResolve(fullTypeNameCandidate, out type))
                || (typeNameCandidate is not null && _typeRegistry.TryResolve(typeNameCandidate, out type));
            if (!typeResolvedFromRegistry)
            {
                if (!hasResponseMessageId)
                {
                    _logger.LogWarning("Unregistered message type '{TypeName}'. Rejecting", fullTypeName);
                    return new ConsumeEventResult { Success = false };
                }

                type = typeof(Message);
            }

            if (replyProcessor != null && hasResponseMessageId)
            {
                var replyResult = await replyProcessor.ProcessAsync(messageBytes, type!, null, headers, envelope, cancellationToken).ConfigureAwait(false);
                if (replyResult == ProcessResult.Handled)
                {
                    return new ConsumeEventResult { Success = true };
                }

                // No pending request matched this reply — the caller timed out or this is a
                // duplicate delivery. Returning Success=false would drive nack/requeue and
                // cause spurious retry/DLQ churn, so we log at Debug and silently ack instead.
                // Reachable only when hasResponseMessageId (see line 131 gate).
                var replyCorrelationId = HeaderDecoder.Decode(headers[HeaderKeys.ResponseMessageId]) ?? "<unknown>";
                _logger.LogDebug(
                    "Discarding reply for untracked correlation '{CorrelationId}' (likely timed out or duplicate)",
                    replyCorrelationId);
                return new ConsumeEventResult { Success = true };
            }

            if (!typeResolvedFromRegistry)
            {
                return new ConsumeEventResult { Success = false };
            }

            var message = _serializer.Deserialize(messageBytes, type!);

            // Build the middleware chain per dispatch from the scoped provider so scoped/transient
            // middleware lifetimes are honoured — a cached chain would pin the first instance for
            // the lifetime of the bus.
            var chain = BuildProcessingChain(scope.ServiceProvider);
            return await chain(messageBytes, type!, message, headers, envelope, cancellationToken).ConfigureAwait(false);
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
            if (proc.RunBeforeDeserialization)
            {
                continue;
            }

            var result = await proc.ProcessAsync(mb, mt, m, h, e, ct).ConfigureAwait(false);
            if (result == ProcessResult.Handled)
            {
                return new ConsumeEventResult { Success = true };
            }
        }

        _logger.LogWarning("No processor handled message of type {MessageType}", mt.FullName);
        return new ConsumeEventResult { Success = true, NotHandled = true };
    }

    private MessageProcessingDelegate BuildProcessingChain(IServiceProvider scopedProvider)
    {
        var middlewareTypes = _pipelineConfig.MessageProcessingMiddleware;
        if (middlewareTypes.Count == 0)
        {
            return RunProcessors;
        }

        MessageProcessingDelegate chain = RunProcessors;
        for (int i = middlewareTypes.Count - 1; i >= 0; i--)
        {
            var mw = (IMessageProcessingMiddleware)scopedProvider.GetRequiredService(middlewareTypes[i]);
            var next = chain;
            chain = (mb, mt, m, h, e, ct) => mw.Process(mb, mt, m, h, e, next, ct);
        }
        return chain;
    }
}
