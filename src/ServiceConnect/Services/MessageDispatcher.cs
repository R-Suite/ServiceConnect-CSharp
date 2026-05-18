using System.Text.Json;
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
internal sealed class MessageDispatcher(
    IMessageSerializer serializer,
    IFilterPipeline filterPipeline,
    IList<IMessageProcessor> processors,
    ILogger<MessageDispatcher> logger,
    IBusConfiguration config,
    IPipelineConfiguration pipelineConfig,
    IServiceScopeFactory scopeFactory,
    ConsumeScopeAccessor scopeAccessor,
    IMessageTypeRegistry typeRegistry,
    ConsumeContextAccessor? consumeContextAccessor = null) : IMessageDispatcher
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
    // Optional so test rigs that construct the dispatcher directly without DI keep working —
    // a null accessor means middleware that resolves the inbound headers via the ambient
    // ConsumeContextAccessor sees null (the pre-existing behaviour). In production wiring,
    // ServiceCollectionExtensions registers ConsumeContextAccessor as a singleton and DI
    // threads it in here automatically.
    private readonly ConsumeContextAccessor? _consumeContextAccessor = consumeContextAccessor;

    /// <inheritdoc />
    public async Task<ConsumeEventResult> DispatchAsync(ReadOnlyMemory<byte> messageBytes, string messageType, IReadOnlyDictionary<string, object> headers, CancellationToken cancellationToken = default)
    {
        // CreateAsyncScope so user-supplied IMessageHandler / IFilter / IMessageProcessingMiddleware
        // implementations that are IAsyncDisposable-only (no IDisposable) are honoured. A sync scope
        // dispose against an IAsyncDisposable-only registered service throws
        // InvalidOperationException("AsyncDisposableServiceNotSupported") under MS.DI. Explicit
        // try/finally + DisposeAsync().ConfigureAwait(false) so the analyzer can see the await.
        var scope = _scopeFactory.CreateAsyncScope();
        try
        {
        using var _ = _scopeAccessor.Push(scope.ServiceProvider);

        Envelope? envelope = null;
        IDisposable? contextScope = null;
        var beforeFiltersRan = false;
        try
        {
            // Downstream pipeline (IMessageProcessor, MessageProcessingDelegate, Envelope.Headers)
            // requires a mutable IDictionary<string,object> for middleware mutation. Fast-path
            // succeeds when the runtime type is Dictionary<,> (the expected hot path); the fallback
            // copy handles non-Dictionary<,> runtime types (e.g. ReadOnlyDictionary<,>).
            var mutableHeaders = headers as IDictionary<string, object>
                ?? new Dictionary<string, object>(headers, StringComparer.Ordinal);

            envelope = new Envelope { Headers = mutableHeaders, Body = messageBytes };

            // Push the inbound-context accessor BEFORE filters/middleware run so any outbound
            // call made from a middleware (e.g. an auto-forward IMessageProcessingMiddleware
            // that invokes Bus.RouteAsync or Bus.SendAsync) reads the inbound hop counter via
            // ConsumeContextAccessor.CurrentHeaders. Without this, middleware sees
            // CurrentHeaders == null and the framework stamps RoutingSlipHopsCompleted=1
            // regardless of the inbound hop count — defeating MaxRoutingSlipHops as the
            // cross-service amplification defence.
            //
            // The Dictionary fast-path covers the production transport (Bus constructs as
            // Dictionary<,>); third-party transports passing a non-Dictionary IDictionary
            // get a defensive shallow copy snapshot so the IReadOnlyDictionary contract is
            // honoured. The HandlerProcessor / ProcessManagerProcessor push later with the
            // pooled context's typed headers, which nests cleanly.
            if (_consumeContextAccessor is not null)
            {
                var headersForContext = mutableHeaders as IReadOnlyDictionary<string, object>
                    ?? new Dictionary<string, object>(mutableHeaders, StringComparer.Ordinal);
                contextScope = _consumeContextAccessor.Push(headersForContext);
            }

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

            var (replyProcessor, preDeserHandled) = await RunPreDeserializationProcessorsAsync(messageBytes, mutableHeaders, envelope, cancellationToken).ConfigureAwait(false);
            if (preDeserHandled)
            {
                // Mirror the reply branch and the handler-success branch: a pre-deserialisation
                // processor (StreamProcessor accepting a packet frame) that returns Handled is
                // a successful consume. User filters built on the OnConsumedSuccessfully stage
                // (dedup-key recording, audit, outbox commit) must observe stream packets here
                // — without this call, dedup filters silently under-count and stream payloads
                // bypass user-installed audit hooks.
                await _filterPipeline.ExecuteOnConsumedSuccessfullyFiltersAsync(envelope, cancellationToken).ConfigureAwait(false);
                return new ConsumeEventResult { Success = true };
            }

            var typeResolution = TryResolveMessageType(messageType, headers, hasResponseMessageId);
            if (typeResolution.ShouldReturnNotHandled)
            {
                return new ConsumeEventResult { Success = true, NotHandled = true };
            }
            var type = typeResolution.Type!;

            if (hasResponseMessageId)
            {
                return await DispatchReplyAsync(replyProcessor, messageBytes, type, mutableHeaders, envelope, headers, cancellationToken).ConfigureAwait(false);
            }

            var message = _serializer.Deserialize(messageBytes, type!);

            // Build the middleware chain per dispatch from the scoped provider so scoped/transient
            // middleware lifetimes are honoured — a cached chain would pin the first instance for
            // the lifetime of the bus.
            var chain = BuildProcessingChain(scope.ServiceProvider);
            var result = await chain(messageBytes, type!, message, mutableHeaders, envelope, cancellationToken).ConfigureAwait(false);

            if (result.Success && !result.NotHandled)
            {
                await RunOnConsumedSuccessfullyAsync(envelope, messageType, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cooperative shutdown — propagate so the outer finally leaves the message unacked
            // for broker redelivery on next start. Not an application error.
            // See learn/operations/cancellation for the full contract.
            throw;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or Interfaces.Exceptions.SerializationException)
        {
            // Permanently malformed payload — JsonException covers wire-format faults
            // (truncated bytes, schema mismatch, max-depth exceeded), NotSupportedException
            // surfaces when an STJ converter rejects the value, and SerializationException
            // is the serializer's wrapper around JsonException (so JsonException would
            // otherwise be invisible behind the wrap — the wrap is what
            // SystemTextJsonMessageSerializer.Deserialize raises on every failed parse).
            // Retrying produces the identical failure; route as terminal so the message goes
            // straight to the error exchange and the retry budget isn't burned on a poison
            // delivery.
            _logger.LogError(ex,
                "Permanently invalid payload for message of type {MessageType}; routing as terminal failure (no retry).",
                messageType);
            await InvokeExceptionHandlerAsync(ex, messageType, cancellationToken).ConfigureAwait(false);
            return new ConsumeEventResult { Success = false, Exception = ex, TerminalFailure = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching message of type {MessageType}", messageType);
            await InvokeExceptionHandlerAsync(ex, messageType, cancellationToken).ConfigureAwait(false);
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
            // Pop the inbound-context AsyncLocal AFTER AfterConsumingFilters so those filters
            // still observe the headers context, but BEFORE the DI scope disposes so any
            // service depending on the accessor doesn't observe a stale push from this
            // dispatch in the next one.
            contextScope?.Dispose();
        }
        }
        finally
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Handles a reply-shaped delivery (ResponseMessageId header present). Two sub-cases:
    //   - replyProcessor != null: dispatch through the reply processor. Both Handled (matched a
    //     pending request) and not-Handled (untracked correlation — likely timed out or duplicate)
    //     are treated as successful consumes; the dispatcher acks either way so the
    //     OnConsumedSuccessfully filter stage must fire on both so audit/telemetry filters count
    //     reply messages.
    //   - replyProcessor == null: misconfiguration. A reply arrived but no IRequestReplyManager
    //     is registered; running the regular handler against a reply payload would surprise
    //     handlers expecting a self-contained message. Log at Warning and ack-and-drop.
    private async Task<ConsumeEventResult> DispatchReplyAsync(
        ReplyProcessor? replyProcessor,
        ReadOnlyMemory<byte> messageBytes,
        Type type,
        IDictionary<string, object> mutableHeaders,
        Envelope envelope,
        IReadOnlyDictionary<string, object> headers,
        CancellationToken cancellationToken)
    {
        if (replyProcessor == null)
        {
            _logger.LogWarning(
                "Reply received (ResponseMessageId={ResponseMessageId}) but no ReplyProcessor / IRequestReplyManager is registered on this bus. " +
                "The reply cannot be correlated and the regular handler must NOT run against a reply payload. " +
                "Acking and dropping.",
                HeaderDecoder.Decode(headers[HeaderKeys.ResponseMessageId]) ?? "<unknown>");
            return new ConsumeEventResult { Success = true };
        }

        var replyResult = await replyProcessor.ProcessAsync(messageBytes, type, null, mutableHeaders, envelope, cancellationToken).ConfigureAwait(false);
        await _filterPipeline.ExecuteOnConsumedSuccessfullyFiltersAsync(envelope, cancellationToken).ConfigureAwait(false);

        if (replyResult == ProcessResult.Handled)
        {
            return new ConsumeEventResult { Success = true };
        }

        _logger.LogDebug(
            "Discarding reply for untracked correlation '{CorrelationId}' (likely timed out or duplicate)",
            HeaderDecoder.Decode(headers[HeaderKeys.ResponseMessageId]) ?? "<unknown>");
        return new ConsumeEventResult { Success = true };
    }

    // Runs the OnConsumedSuccessfully filter stage after a successful handler dispatch. A throw
    // here flips the dispatch result from success to fail and the consumer host retries — which
    // re-runs the already-successful handler and DUPLICATES its side effects. The discriminating
    // Error log makes that signal visible so operators can tell a post-handler-filter-throw
    // apart from a handler-throw when triaging dedup failures. Cooperative-shutdown OCE is
    // allowed to propagate without the side-effect-duplication note.
    private async Task RunOnConsumedSuccessfullyAsync(Envelope envelope, string messageType, CancellationToken cancellationToken)
    {
        try
        {
            await _filterPipeline.ExecuteOnConsumedSuccessfullyFiltersAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception successFilterEx) when (successFilterEx is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(successFilterEx,
                "OnConsumedSuccessfully filter threw after handler success for message of type {MessageType}; retry will re-run the handler and duplicate its side effects.",
                messageType);
            throw;
        }
    }

    // Invokes the optional ExceptionHandler callback and swallows + logs any throw it produces
    // at Error level. ExceptionHandler is an opt-in user-configured notification hook; a crash
    // inside it is a real failure of an explicitly-installed surface and operators must see it.
    // The dispatcher continues regardless — the original dispatch exception is already attached
    // to the returned ConsumeEventResult and drives the retry/error-queue path; the hook crash
    // is a secondary signal that must not block message processing.
    private async Task InvokeExceptionHandlerAsync(Exception ex, string messageType, CancellationToken cancellationToken)
    {
        if (_config.ExceptionHandler is not { } handler)
        {
            return;
        }
        try
        {
            await handler(ex, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception handlerEx)
        {
            _logger.LogError(handlerEx, "ExceptionHandler threw while handling dispatch error for message of type {MessageType}", messageType);
        }
    }

    /// <summary>
    /// Outcome of resolving the inbound delivery's message-type. When <see cref="Type"/> is non-null
    /// the caller dispatches against it (it may be <c>typeof(Message)</c> on the reply-path fallback);
    /// when <see cref="ShouldReturnNotHandled"/> is true the caller returns a Success+NotHandled
    /// result immediately (terminal — no retry, no dispatch).
    /// </summary>
    private readonly record struct MessageTypeResolution(Type? Type, bool ShouldReturnNotHandled);

    /// <summary>
    /// Resolves the type to dispatch against from the inbound delivery's <paramref name="messageType"/>
    /// argument and the FullTypeName / TypeName headers, falling back to <c>typeof(Message)</c> for
    /// the reply path (<paramref name="hasResponseMessageId"/> == true) when the registry doesn't
    /// know the type.
    /// </summary>
    /// <remarks>
    /// Unregistered + not-a-reply is a terminal not-handled: retrying never resolves it, so the caller
    /// routes to dead-letter (when configured) or ack-and-drops rather than burning the full retry
    /// budget through Success=false → nack/requeue.
    /// </remarks>
    private MessageTypeResolution TryResolveMessageType(
        string messageType,
        IReadOnlyDictionary<string, object> headers,
        bool hasResponseMessageId)
    {
        string? primaryCandidate = string.IsNullOrWhiteSpace(messageType) ? null : messageType;
        string? fullTypeNameCandidate = headers.TryGetValue(HeaderKeys.FullTypeName, out var fullTypeNameRaw)
            ? HeaderDecoder.Decode(fullTypeNameRaw) : null;
        string? typeNameCandidate = headers.TryGetValue(HeaderKeys.TypeName, out var typeNameRaw)
            ? HeaderDecoder.Decode(typeNameRaw) : null;

        if (primaryCandidate is null && fullTypeNameCandidate is null && typeNameCandidate is null)
        {
            throw new InvalidOperationException(
                "Message is missing type information: messageType parameter is empty and neither FullTypeName nor TypeName header is present.");
        }

        var fullTypeName = primaryCandidate ?? fullTypeNameCandidate ?? typeNameCandidate!;

        Type? type = null;
        bool typeResolvedFromRegistry =
            (primaryCandidate is not null && _typeRegistry.TryResolve(primaryCandidate, out type))
            || (fullTypeNameCandidate is not null && _typeRegistry.TryResolve(fullTypeNameCandidate, out type))
            || (typeNameCandidate is not null && _typeRegistry.TryResolve(typeNameCandidate, out type));

        if (typeResolvedFromRegistry)
        {
            return new MessageTypeResolution(type, ShouldReturnNotHandled: false);
        }

        if (hasResponseMessageId)
        {
            // Reply with unregistered payload type: the dispatcher can still route the reply via
            // DispatchReplyAsync. The base Message type is the conservative deserialise target;
            // the matched pending request's ReplyType supplies the real shape downstream.
            return new MessageTypeResolution(typeof(Message), ShouldReturnNotHandled: false);
        }

        _logger.LogWarning("Unregistered message type '{TypeName}'. Routing as not-handled.", fullTypeName);
        return new MessageTypeResolution(Type: null, ShouldReturnNotHandled: true);
    }

    // Iterates all processors. Pre-deserialization processors run immediately; ReplyProcessor
    // is pulled out and returned as a typed reference for the dispatch routing logic. Returns
    // (replyProcessor, true) when a pre-deserialization processor signals Handled so the caller
    // can short-circuit without entering the rest of the dispatch path.
    private async Task<(ReplyProcessor? ReplyProcessor, bool Handled)> RunPreDeserializationProcessorsAsync(
        ReadOnlyMemory<byte> messageBytes, IDictionary<string, object> headers, Envelope envelope, CancellationToken cancellationToken)
    {
        ReplyProcessor? replyProcessor = null;
        foreach (var proc in _processors)
        {
            // Honour cooperative shutdown between processors. A processor that completes
            // synchronously (no internal await) would otherwise not observe cancellation
            // until the next async point — on a busy broker that could be the next message.
            cancellationToken.ThrowIfCancellationRequested();

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
                return (replyProcessor, true);
            }
        }

        return (replyProcessor, false);
    }

    private async Task<ConsumeEventResult> RunProcessors(
        ReadOnlyMemory<byte> messageBytes,
        Type messageType,
        object message,
        IDictionary<string, object> headers,
        Envelope envelope,
        CancellationToken cancellationToken)
    {
        foreach (var proc in _processors)
        {
            // Mirror the pre-deserialization loop: shutdown cancellation must propagate
            // between processors even when a processor completes synchronously.
            cancellationToken.ThrowIfCancellationRequested();

            if (proc.RunBeforeDeserialization)
            {
                continue;
            }

            var result = await proc.ProcessAsync(messageBytes, messageType, message, headers, envelope, cancellationToken).ConfigureAwait(false);
            if (result == ProcessResult.Handled)
            {
                return new ConsumeEventResult { Success = true };
            }
        }

        // Debug, not Warning: the unregistered-type path already logs at Warning earlier in
        // DispatchAsync. Reaching here means the type IS registered but no processor (handler,
        // saga, aggregator, stream) claimed it — on a topic-exchange topology where the bus
        // binds an exchange it doesn't fully service, that's the steady state for the unclaimed
        // subset, not an operator-actionable signal.
        _logger.LogDebug("No processor handled message of type {MessageType}", messageType.FullName);
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
            chain = (messageBytes, messageType, message, headers, envelope, cancellationToken) =>
                mw.ProcessAsync(messageBytes, messageType, message, headers, envelope, next, cancellationToken);
        }
        return chain;
    }
}
