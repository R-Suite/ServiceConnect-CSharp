using System.Diagnostics;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Telemetry;

// Telemetry uses the ServiceConnect.Interfaces event-args types.

/// <summary>
/// Creates publish, send, and consume activities for ServiceConnect message operations.
/// </summary>
public static class ServiceConnectActivitySource
{
    internal static readonly Version? Version = typeof(ServiceConnectActivitySource).Assembly.GetName().Version;

    /// <summary>
    /// Gets the activity-source name used for all publish, send, and consume spans.
    /// Register listeners via <c>AddSource("ServiceConnect.Bus")</c>.
    /// </summary>
    public static readonly string ActivitySourceName = (typeof(ServiceConnectActivitySource).Assembly.GetName().Name ?? "ServiceConnect") + ".Bus";

    private static readonly ActivitySource _activitySource = new(ActivitySourceName, Version?.ToString() ?? "0.0.0");

    /// <summary>
    /// Disposes the underlying <see cref="ActivitySource"/>. Call only when unloading
    /// the assembly in a collectible <c>AssemblyLoadContext</c>; for normal long-running
    /// processes the source lives for process lifetime and disposal is unnecessary.
    /// </summary>
    internal static void Shutdown() => _activitySource.Dispose();

    /// <summary>
    /// Starts a publish-side activity. Returns <c>null</c> when no listeners are
    /// registered for <see cref="ActivitySourceName"/>.
    /// </summary>
    public static Activity? Publish(
        PublishEventArgs eventArgs,
        ServiceConnectInstrumentationOptions options,
        IMessagingSystemAttributes attributes,
        ActivityContext parentContext = default)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(attributes);

        Activity? activity = StartActivityWithParent(
            _activitySource,
            ActivitySourceName,
            ActivityKind.Producer,
            options.EnablePublishTelemetry,
            attributes,
            "publish",
            parentContext);

        if (activity is null)
        {
            // Single inject. No activity → propagate ambient context for downstream linking.
            InjectTraceContext(Activity.Current, eventArgs.Headers);
            return null;
        }

        try
        {
            // Single inject. Activity non-null → propagate the new span's context.
            InjectTraceContext(activity, eventArgs.Headers);

            if (activity.IsAllDataRequested)
            {
                if (eventArgs.Message?.CorrelationId is { } cid && cid != Guid.Empty)
                {
                    activity.SetTag(MessagingAttributes.MessageConversationId,
                        Truncate(cid.ToString(), options.MaxTagValueLength));
                }

                if (!string.IsNullOrWhiteSpace(eventArgs.Exchange))
                {
                    // Truncate the exchange first, then append the suffix. Concatenating first
                    // and truncating second would chop off the " publish" suffix when the
                    // exchange is close to MaxTagValueLength — losing the operation signal in
                    // the span display.
                    var truncatedExchange = Truncate(eventArgs.Exchange, options.MaxTagValueLength);
                    activity.DisplayName = truncatedExchange + " publish";
                    activity.SetTag(MessagingAttributes.MessagingDestination, truncatedExchange);
                }
                else
                {
                    activity.DisplayName = "anonymous publish";
                    // OTel messaging semconv defines messaging.destination.anonymous as a
                    // boolean attribute; emitting the string "true" mismatches downstream
                    // schema-validating backends (Honeycomb, Tempo, Jaeger v2).
                    activity.SetTag(MessagingAttributes.MessagingDestinationAnonymous, true);
                }

                if (!string.IsNullOrWhiteSpace(eventArgs.RoutingKey))
                {
                    activity.SetTag(MessagingAttributes.MessagingDestinationRoutingKey,
                        Truncate(eventArgs.RoutingKey, options.MaxTagValueLength));
                }

                if (eventArgs.Headers.TryGetValue(HeaderKeys.MessageId, out string? messageId))
                {
                    activity.SetTag(MessagingAttributes.MessageId,
                        Truncate(messageId, options.MaxTagValueLength));
                }
            }

            TryEnrich(activity, eventArgs.Message, options);

            return activity;
        }
        catch
        {
            activity.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Starts a consume-side activity, extracting the W3C traceparent/tracestate
    /// from <paramref name="eventArgs"/>.Headers so the resulting span is linked to
    /// the publishing activity. Returns <c>null</c> when no listeners are registered
    /// for <see cref="ActivitySourceName"/>.
    /// </summary>
    public static Activity? Consume(
        ConsumeEventArgs eventArgs,
        ServiceConnectInstrumentationOptions options,
        IMessagingSystemAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(attributes);

        DistributedContextPropagator.Current.ExtractTraceIdAndState(eventArgs.Headers, ExtractTraceIdAndState, out string? traceId, out string? traceState);
        bool malformedTraceparent = false;
        if (!ActivityContext.TryParse(traceId, traceState, out ActivityContext parentContext))
        {
            // Distinguish "no traceparent header at all" from "traceparent header present
            // but unparseable". DistributedContextPropagator's W3C implementation validates
            // the traceparent shape and returns null when the on-wire value is malformed,
            // collapsing both cases into traceId == null at this layer. Probe the raw
            // headers directly to recover the distinction:
            //   - header absent : fall through to ambient Activity.Current or the AsyncLocal
            //     fallback (normal cross-process linking).
            //   - header present but malformed : force a fresh trace root so the span isn't
            //     incorrectly parented onto whatever the hosting environment happens to have
            //     as Activity.Current (e.g. an ASP.NET request or host-worker activity
            //     wrapping the consume loop). Stamp a diagnostic tag so operators can spot
            //     poisoned producers in search.
            malformedTraceparent = traceId is not null || HasTraceparentHeader(eventArgs.Headers);
            parentContext = default;
        }

        Activity? activity = StartActivityWithParent(
            _activitySource,
            ActivitySourceName,
            ActivityKind.Consumer,
            options.EnableConsumeTelemetry,
            attributes,
            "process",
            parentContext,
            forceFreshRoot: malformedTraceparent);

        if (activity is null)
        {
            return null;
        }

        // Telemetry enrichment is best-effort: a malformed header (HeaderDecoder throw on a
        // pathologically nested AMQP table, an unexpected runtime tag failure) must NOT
        // block the consume pipeline. Telemetry runs BEFORE the handler dispatch, so a
        // rethrow here propagates out of the middleware before next() is invoked and the
        // handler never runs — a single poison header would crash every consumer pulling
        // it. Capture the failure into the span via an enrichment.exception tag and
        // return the started activity so the dispatch path proceeds.
        try
        {
            if (activity.IsAllDataRequested)
            {
                // Targeted header lookups — decode only the headers actually used here
                // rather than allocating a full decode dictionary for all 15-20 headers.
                string? destinationAddress = eventArgs.Headers.TryGetValue(HeaderKeys.DestinationAddress, out var daVal)
                    ? HeaderDecoder.Decode(daVal) : null;
                string? messageId = eventArgs.Headers.TryGetValue(HeaderKeys.MessageId, out var miVal)
                    ? HeaderDecoder.Decode(miVal) : null;
                string? correlationId = eventArgs.Headers.TryGetValue(HeaderKeys.CorrelationId, out var ciVal)
                    ? HeaderDecoder.Decode(ciVal) : null;

                // Truncate the destination first, then append the suffix. Concatenating first
                // and truncating second would chop off the " process" suffix when the
                // destination is at MaxTagValueLength — losing the operation signal in the
                // span display.
                var truncatedDest = Truncate(string.IsNullOrWhiteSpace(destinationAddress) ? "anonymous" : destinationAddress, options.MaxTagValueLength);
                activity.DisplayName = truncatedDest + " process";

                if (messageId is not null)
                {
                    activity.SetTag(MessagingAttributes.MessageId,
                        Truncate(messageId, options.MaxTagValueLength));
                }

                if (correlationId is not null)
                {
                    activity.SetTag(MessagingAttributes.MessageConversationId,
                        Truncate(correlationId, options.MaxTagValueLength));
                }

                if (!string.IsNullOrEmpty(destinationAddress))
                {
                    activity.SetTag(MessagingAttributes.MessagingDestination,
                        Truncate(destinationAddress, options.MaxTagValueLength));
                }
                else
                {
                    activity.SetTag(MessagingAttributes.MessagingDestinationAnonymous, true);
                }
            }

            // BodySize is the on-wire byte count, populated by the consume middleware even
            // when eventArgs.Message is the empty-sentinel array (no enricher configured —
            // bytes were not materialised to save the per-delivery allocation). Fall back to
            // eventArgs.Message.Length for direct callers of Consume() that pre-date BodySize
            // and only set Message; without the fallback, those callers would suddenly emit
            // body-size=0 on every span.
            var bodySize = eventArgs.BodySize > 0
                ? eventArgs.BodySize
                : (eventArgs.Message?.Length ?? 0);
            activity.SetTag(MessagingAttributes.MessagingBodySize, bodySize);
            if (eventArgs.Message is { Length: > 0 })
            {
                TryEnrich(activity, eventArgs.Message, options);
            }

            return activity;
        }
        catch (OperationCanceledException)
        {
            activity.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            // Don't rethrow — see the block-leading comment. Record the failure as a
            // telemetry-attribution tag (type name only; messages may carry caller-
            // controlled payloads) and return the partial activity. Wrap the SetTag
            // in its own try/catch because a pathological ActivityListener registered
            // against this source could itself throw during the tag callback — without
            // the inner guard, a malformed-header poison delivery would still kill the
            // consume path via the listener rather than the original enrichment fault.
            try
            {
                activity.SetTag("enrichment.exception", ex.GetType().FullName);
            }
            catch
            {
                // Telemetry is best-effort. Last-resort: leave the activity unmodified.
            }
            return activity;
        }
    }

    /// <summary>
    /// Starts a send-side activity. Returns <c>null</c> when no listeners are
    /// registered for <see cref="ActivitySourceName"/>.
    /// </summary>
    public static Activity? Send(
        SendEventArgs eventArgs,
        ServiceConnectInstrumentationOptions options,
        IMessagingSystemAttributes attributes,
        ActivityContext parentContext = default)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(attributes);

        // SendAsync writes to a specific queue (point-to-point), but in OTel messaging
        // semantic conventions that is still classified as "publish" — the producer-side
        // operation name. The point-to-point distinction is preserved by the shared
        // _activitySource and the per-destination DisplayName ("<queue> send"), so
        // backends that need to disaggregate send from publish can filter by activity name.
        Activity? activity = StartActivityWithParent(
            _activitySource,
            ActivitySourceName,
            ActivityKind.Producer,
            options.EnableSendTelemetry,
            attributes,
            "publish",
            parentContext);

        if (activity is null)
        {
            // Single inject. No activity → propagate ambient context for downstream linking.
            InjectTraceContext(Activity.Current, eventArgs.Headers);
            return null;
        }

        try
        {
            // Single inject. Activity non-null → propagate the new span's context.
            InjectTraceContext(activity, eventArgs.Headers);

            if (activity.IsAllDataRequested)
            {
                // SendEventArgs carries the per-delivery endpoint only — for multi-endpoint
                // fan-out (SendToManyAsync), each delivery raises its own SendEventArgs and
                // therefore its own span. The fan-out grouping is recoverable via the message
                // CorrelationId, which stays stable across the deliveries.
                var destination = string.IsNullOrWhiteSpace(eventArgs.EndPoint) ? null : eventArgs.EndPoint;

                // Truncate the destination first, then append the suffix. Concatenating first
                // and truncating second would chop off " send" when the endpoint is near the
                // MaxTagValueLength cap — losing the operation signal in the span display.
                if (destination is not null)
                {
                    var truncatedDestination = Truncate(destination, options.MaxTagValueLength);
                    activity.DisplayName = truncatedDestination + " send";
                    activity.SetTag(MessagingAttributes.MessagingDestination, truncatedDestination);
                }
                else
                {
                    activity.DisplayName = "anonymous send";
                    activity.SetTag(MessagingAttributes.MessagingDestinationAnonymous, true);
                }

                if (eventArgs.Message is null)
                {
                    return activity;
                }

                if (eventArgs.Message.CorrelationId != Guid.Empty)
                {
                    activity.SetTag(MessagingAttributes.MessageConversationId,
                        Truncate(eventArgs.Message.CorrelationId.ToString(), options.MaxTagValueLength));
                }
            }
            else if (eventArgs.Message is null)
            {
                return activity;
            }

            TryEnrich(activity, eventArgs.Message, options);

            return activity;
        }
        catch
        {
            activity.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Marks <paramref name="activity"/> as errored with OTel-semantic-convention exception metadata.
    /// No-op when <paramref name="activity"/> is null, so callers don't need their own null guards.
    /// </summary>
    /// <remarks>
    /// Call from inside a catch block (immediately before <c>throw</c>) so the activity's status
    /// description reflects the real failure. Exception messages may contain sensitive content
    /// (connection strings, user data) — trace-sanitisation is the caller's responsibility.
    /// Not currently wired up by the Bus/Producer/Consumer host paths; exposed as a public
    /// integration point for downstream consumers instrumenting their own handler pipelines.
    /// </remarks>
    public static void SetError(Activity? activity, Exception exception, ServiceConnectInstrumentationOptions options)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(options);

        if (activity is null)
        {
            return;
        }

        var message = options.ExceptionMessageSanitiser is { } sanitise
            ? sanitise(exception)
            : exception.Message;

        activity.SetStatus(ActivityStatusCode.Error, message);

#if NET9_0_OR_GREATER
        if (options.ExceptionMessageSanitiser is null)
        {
            // No sanitiser — use the framework's AddException (records the raw message).
            activity.AddException(exception);
        }
        else
        {
            // Sanitiser supplied — opt out of AddException (would re-record the unsanitised
            // message). Record the OTel "exception" event manually with the sanitised message.
            // Use StackTrace directly rather than ToString() because ToString() includes the
            // formatted Message, which would bypass the sanitiser and leak the raw message.
            activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                ["exception.type"] = exception.GetType().FullName,
                ["exception.message"] = message,
                ["exception.stacktrace"] = exception.StackTrace ?? string.Empty,
            }));
        }
#else
        // .NET 8 fallback: record the OTel semantic-convention "exception" event manually.
        // Use StackTrace directly rather than ToString() because ToString() includes the
        // formatted Message, which would bypass the sanitiser and leak the raw message.
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.type"] = exception.GetType().FullName,
            ["exception.message"] = message,
            ["exception.stacktrace"] = exception.StackTrace ?? string.Empty,
        }));
#endif
    }

    /// <summary>
    /// Attempts to parse a W3C trace context from the supplied headers. Returns
    /// <c>true</c> and populates <paramref name="context"/> when the headers contain
    /// a well-formed traceparent; otherwise returns <c>false</c>.
    /// </summary>
    public static bool TryGetExistingContext(IDictionary<string, string> headers, out ActivityContext context)
    {
        if (headers == null)
        {
            context = default;
            return false;
        }

        DistributedContextPropagator.Current.ExtractTraceIdAndState(
            headers, ExtractTraceIdAndState,
            out string? traceParent, out string? traceState);
        return ActivityContext.TryParse(traceParent, traceState, out context);
    }

    /// <summary>
    /// Probes the carrier headers for a "traceparent" key in the same dictionary shapes
    /// <see cref="ExtractTraceIdAndState"/> understands. Used to distinguish "no traceparent
    /// on the wire" from "traceparent present but rejected by the W3C propagator" — both
    /// surface as a null traceId at the propagator boundary, but only the second deserves
    /// a malformed-header diagnostic and a forced fresh trace root.
    /// </summary>
    private static bool HasTraceparentHeader(object? headers) => headers switch
    {
        IDictionary<string, object> objHeaders => objHeaders.ContainsKey(TraceParentHeaderName),
        IReadOnlyDictionary<string, object> roObjHeaders => roObjHeaders.ContainsKey(TraceParentHeaderName),
        IDictionary<string, string> strHeaders => strHeaders.ContainsKey(TraceParentHeaderName),
        IReadOnlyDictionary<string, string> roStrHeaders => roStrHeaders.ContainsKey(TraceParentHeaderName),
        _ => false,
    };

    private static void ExtractTraceIdAndState(object? eventArgs, string name, out string? value, out IEnumerable<string>? values)
    {
        values = default;

        // Iterate via the interface, not concrete Dictionary<,>. ConsumeContext
        // wraps headers as ReadOnlyDictionary<string, object>, which the old
        // concrete-type switch did not recognise — so every consume span arrived
        // without its traceparent and restarted the trace. Check the object
        // variant first (matches the raw header bag off the wire) then fall
        // back to a string-keyed dictionary for already-decoded headers.
        switch (eventArgs)
        {
            case IDictionary<string, object> objHeaders when objHeaders.TryGetValue(name, out object? objVal):
                value = HeaderDecoder.Decode(objVal);
                return;
            case IReadOnlyDictionary<string, object> roObjHeaders when roObjHeaders.TryGetValue(name, out object? roObjVal):
                value = HeaderDecoder.Decode(roObjVal);
                return;
            // string branch: values are already decoded; HeaderDecoder.Decode is for byte[] RabbitMQ headers only.
            case IDictionary<string, string> strHeaders when strHeaders.TryGetValue(name, out string? strVal):
                value = strVal;
                return;
            case IReadOnlyDictionary<string, string> roStrHeaders when roStrHeaders.TryGetValue(name, out string? roStrVal):
                value = roStrVal;
                return;
            default:
                value = default;
                return;
        }
    }

    /// <summary>
    /// Writes the current activity's W3C trace context into the outgoing-headers dictionary
    /// so downstream consumers can link their consume span to the originating publish. Mirrors
    /// <see cref="Consume"/>'s extract side; without injection, each consume span becomes a
    /// new trace root and the end-to-end graph cannot be stitched across the broker.
    /// </summary>
    /// <remarks>
    /// When <see cref="Activity.Current"/> is null, falls back to the inbound-context snapshot
    /// stashed by <see cref="TelemetryProcessingMiddleware"/> when consume telemetry is disabled
    /// but publish telemetry is enabled. Without that fallback, a configuration of
    /// <c>EnableConsumeTelemetry=false ∧ EnablePublishTelemetry=true</c> would silently snap
    /// the trace at every consume hop because the consume side never produces an
    /// <c>Activity.Current</c> for the publish side to read.
    /// </remarks>
    private static void InjectTraceContext(Activity? activity, IDictionary<string, string> headers)
    {
        if (activity is not null)
        {
            DistributedContextPropagator.Current.Inject(activity, headers, InjectHeader);
            return;
        }

        if (_inboundTraceFallback.Value is { } fallback)
        {
            // Pass through the original publisher's traceparent verbatim. Downstream
            // consumers see the original publisher as their parent, skipping our
            // untraced consume hop — preferable to starting a fresh trace.
            headers[TraceParentHeaderName] = fallback.TraceParent;
            if (!string.IsNullOrEmpty(fallback.TraceState))
            {
                headers[TraceStateHeaderName] = fallback.TraceState!;
            }
        }
    }

    // W3C field names. DistributedContextPropagator uses these for its W3C propagator,
    // which is the default and effectively standard. Hard-coding here keeps the fallback
    // path independent of the propagator instance — if a user installs a non-W3C
    // propagator, the fallback still emits W3C, which is the dominant on-wire format.
    private const string TraceParentHeaderName = "traceparent";
    private const string TraceStateHeaderName = "tracestate";

    private static readonly AsyncLocal<InboundTraceSnapshot?> _inboundTraceFallback = new();

    internal readonly record struct InboundTraceSnapshot(string TraceParent, string? TraceState);

    /// <summary>
    /// Stashes the inbound traceparent/tracestate so a publish on the same logical message
    /// flow can continue the trace even when consume telemetry is disabled. The middleware
    /// must clear the value in a finally to avoid bleeding context into unrelated work.
    /// </summary>
    internal static IDisposable SetInboundTraceFallback(string traceParent, string? traceState)
    {
        var prior = _inboundTraceFallback.Value;
        _inboundTraceFallback.Value = new InboundTraceSnapshot(traceParent, traceState);
        return new InboundTraceFallbackScope(prior);
    }

    private sealed class InboundTraceFallbackScope(InboundTraceSnapshot? prior) : IDisposable
    {
        private InboundTraceSnapshot? _prior = prior;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _inboundTraceFallback.Value = _prior;
            _prior = default;
        }
    }

    private static ActivityContext TryResolveFallbackParentContext()
    {
        if (_inboundTraceFallback.Value is not { } fallback)
        {
            return default;
        }
        if (!ActivityContext.TryParse(fallback.TraceParent, fallback.TraceState, out var context))
        {
            return default;
        }
        return context;
    }

    private static int _warnedAboutCarrierShape;

    private static void InjectHeader(object? carrier, string fieldName, string fieldValue)
    {
        if (carrier is IDictionary<string, string> headers)
        {
            headers[fieldName] = fieldValue;
            return;
        }

        if (Interlocked.CompareExchange(ref _warnedAboutCarrierShape, 1, 0) == 0)
        {
            // Once-per-process diagnostic — a refactor that changes the carrier type
            // silently disables trace propagation. Use Trace because static helpers
            // don't have an ILogger; OTel users routinely route .NET trace listeners.
            Trace.TraceWarning(
                "ServiceConnectActivitySource.InjectHeader: unsupported carrier type {0}; trace context not propagated.",
                carrier?.GetType().FullName ?? "<null>");
        }
    }

    internal static string Truncate(string? value, int maxLength)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (maxLength <= 0 || value.Length <= maxLength)
        {
            return value;
        }

        // value[..maxLength] cuts on a UTF-16 code-unit boundary. If position
        // maxLength falls inside a surrogate pair (high surrogate at maxLength-1,
        // low surrogate at maxLength), the slice orphans the high surrogate and
        // OTLP exporters emit invalid UTF-8. Trim one extra char in that case.
        var end = maxLength;
        if (char.IsHighSurrogate(value[end - 1]))
        {
            end--;
        }

        return value[..end];
    }

    // Test seams — internal so the unit-test project can exercise the warning path.
    internal static void InvokeInjectHeaderForTest(object? carrier, string fieldName, string fieldValue) =>
        InjectHeader(carrier, fieldName, fieldValue);
    internal static void ResetCarrierWarnedFlagForTest() =>
        Interlocked.Exchange(ref _warnedAboutCarrierShape, 0);

    private static Activity? StartActivityWithParent(
        ActivitySource activitySource,
        string activityName,
        ActivityKind kind,
        bool enabled,
        IMessagingSystemAttributes attributes,
        string operation,
        ActivityContext parentContext,
        bool forceFreshRoot = false)
    {
        if (!enabled || !activitySource.HasListeners())
        {
            return null;
        }

        // If the caller didn't supply a parent context and Activity.Current is null
        // (consume telemetry disabled but publish on, no ambient activity), fall back
        // to the inbound-trace AsyncLocal so the new activity is parented on the
        // original publisher's traceId. Without this, the new activity becomes a fresh
        // trace root and downstream consumers cannot stitch the graph across this hop.
        // EXCEPTION: when the caller explicitly requested a fresh root (malformed
        // traceparent on the wire), skip the fallback and every other parent source —
        // start a brand-new trace so a poisoned producer cannot graft a bogus span onto
        // an unrelated ambient activity.
        if (!forceFreshRoot && parentContext == default && Activity.Current is null)
        {
            parentContext = TryResolveFallbackParentContext();
        }

        Activity? activity;
        if (forceFreshRoot)
        {
            // Neither StartActivity overload accepts a "force a brand-new trace root"
            // signal directly: passing parentId=null or default(ActivityContext) still
            // falls through to Activity.Current as the implicit parent. Suppress
            // Activity.Current for the StartActivity call so the new span is genuinely
            // rooted.
            //
            // On success, leave Activity.Current = freshRoot (the BCL has set it).
            // This is load-bearing: a consume-side caller will run the user's handler
            // under this ambient, and any Bus.Publish / Bus.Send issued from the handler
            // must read Activity.Current as the fresh root so the outbound traceparent
            // carries the new trace, not the host ambient that the malformed inbound
            // traceparent was trying to graft onto. The eventual Dispose() of the
            // returned activity calls Activity.Stop(), which sets
            // Activity.Current = freshRoot.Parent (null) — the correct end state once
            // consume processing is done. Restore the prior ambient only on the failure
            // paths (StartActivity throw, sampler-drop returning null), where no fresh
            // root exists to flow forward.
            var prior = Activity.Current;
            Activity.Current = null;
            try
            {
                activity = activitySource.StartActivity(activityName, kind, parentContext: default);
            }
            catch
            {
                Activity.Current = prior;
                throw;
            }

            if (activity is null)
            {
                Activity.Current = prior;
                return null;
            }
        }
        else
        {
            activity = activitySource.StartActivity(activityName, kind, parentContext);
            if (activity is null)
            {
                return null;
            }
        }

        if (forceFreshRoot)
        {
            // Diagnostic tag — operators searching for poisoned producers can filter on this.
            activity.SetTag("enrichment.malformed_traceparent", true);
        }

        if (activity.IsAllDataRequested)
        {
            // Map the implementation-specific operation name to the OTel-defined operation type.
            // OTel defines "publish" | "receive" | "process". Consume spans use "process"
            // because ServiceConnect emits them during handler dispatch, not during broker
            // polling ("receive" is the broker-poll side). Send-side spans are "publish".
            var operationType = operation switch
            {
                "process" => "process",
                _ => "publish",  // "publish", "send", "request" all map to OTel "publish"
            };

            activity
                .SetTag(MessagingAttributes.MessagingSystem, attributes.MessagingSystem)
                .SetTag(MessagingAttributes.ProtocolName, attributes.ProtocolName)
                .SetTag(MessagingAttributes.MessagingOperationType, operationType)
                .SetTag(MessagingAttributes.MessagingOperationName, operation);

            // server.address and server.port are required by the OTel messaging semconv for
            // correlation across multi-broker deployments. Emit only when the value is known;
            // skipping an empty address avoids polluting spans with a meaningless empty string.
            var serverAddress = attributes.ServerAddress;
            if (!string.IsNullOrEmpty(serverAddress))
            {
                activity.SetTag(MessagingAttributes.ServerAddress, serverAddress);
            }

            var serverPort = attributes.ServerPort;
            if (serverPort > 0)
            {
                activity.SetTag(MessagingAttributes.ServerPort, serverPort);
            }
        }

        return activity;
    }

    private static void TryEnrich(Activity activity, Message? message, ServiceConnectInstrumentationOptions options)
    {
        if (message is null)
        {
            return;
        }

        try
        {
            options.EnrichWithMessage?.Invoke(activity, message);
        }
        catch (OperationCanceledException)
        {
            // Co-operative cancellation — propagate so callers can distinguish
            // shutdown from enrichment failure.
            throw;
        }
        catch (Exception ex)
        {
            // Tag the exception type only. Message strings can contain caller-
            // controlled payloads or PII; the type name is sufficient diagnostic.
            activity.SetTag("enrichment.exception", ex.GetType().FullName);
        }
    }

    private static void TryEnrich(Activity activity, byte[]? message, ServiceConnectInstrumentationOptions options)
    {
        if (message is null)
        {
            return;
        }

        try
        {
            options.EnrichWithMessageBytes?.Invoke(activity, message);
        }
        catch (OperationCanceledException)
        {
            // See Message overload for rationale on OCE rethrow.
            throw;
        }
        catch (Exception ex)
        {
            // See Message overload for rationale on tagging the type only.
            activity.SetTag("enrichment.exception", ex.GetType().FullName);
        }
    }

    internal static bool IsPublishTelemetryEnabled(ServiceConnectInstrumentationOptions options)
        => options.EnablePublishTelemetry && _activitySource.HasListeners();

    internal static bool IsSendTelemetryEnabled(ServiceConnectInstrumentationOptions options)
        => options.EnableSendTelemetry && _activitySource.HasListeners();

    internal static bool IsConsumeTelemetryEnabled(ServiceConnectInstrumentationOptions options)
        => options.EnableConsumeTelemetry && _activitySource.HasListeners();

    internal static void InvokeTryEnrichForTest(Activity activity, Message? message, ServiceConnectInstrumentationOptions options) =>
        TryEnrich(activity, message, options);

    internal static void InvokeTryEnrichForTest(Activity activity, byte[]? bytes, ServiceConnectInstrumentationOptions options) =>
        TryEnrich(activity, bytes, options);
}
