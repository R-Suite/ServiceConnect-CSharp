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
    public static void Shutdown() => _activitySource.Dispose();

    /// <summary>
    /// Starts a publish-side activity. Returns <c>null</c> when no listeners are
    /// registered for <see cref="ActivitySourceName"/>.
    /// </summary>
    public static Activity? Publish(
        PublishEventArgs eventArgs,
        ServiceConnectInstrumentationOptions options,
        IMessagingSystemAttributes attributes,
        ActivityContext linkedContext = default)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(attributes);

        Activity? activity = StartActivityWithLink(
            _activitySource,
            ActivitySourceName,
            ActivityKind.Producer,
            options.EnablePublishTelemetry,
            attributes,
            "publish",
            linkedContext);

        if (activity is null)
        {
            // L13: single inject. No activity → propagate ambient context for downstream linking.
            InjectTraceContext(Activity.Current, eventArgs.Headers);
            return null;
        }

        try
        {
            // L13: single inject. Activity non-null → propagate the new span's context.
            InjectTraceContext(activity, eventArgs.Headers);

            activity.SetTag(MessagingAttributes.MessageConversationId,
                Truncate(eventArgs.Message?.CorrelationId.ToString(), options.MaxTagValueLength));

            if (!string.IsNullOrWhiteSpace(eventArgs.Exchange))
            {
                activity.DisplayName = Truncate(eventArgs.Exchange + " publish", options.MaxTagValueLength);
                activity.SetTag(MessagingAttributes.MessagingDestination,
                    Truncate(eventArgs.Exchange, options.MaxTagValueLength));
            }
            else
            {
                activity.DisplayName = "anonymous publish";
                activity.SetTag(MessagingAttributes.MessagingDestinationAnonymous, "true");
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
        if (!ActivityContext.TryParse(traceId, traceState, out ActivityContext parentContext))
        {
            // Malformed traceparent — fall through with default parentContext;
            // ActivitySource.StartActivity then picks Activity.Current as the parent.
            parentContext = default;
        }

        Activity? activity = StartActivityWithParent(
            _activitySource,
            ActivitySourceName,
            ActivityKind.Consumer,
            options.EnableConsumeTelemetry,
            attributes,
            "receive",
            parentContext);

        if (activity is null)
        {
            return null;
        }

        try
        {
            // Targeted header lookups — decode only the headers actually used here
            // rather than allocating a full decode dictionary for all 15-20 headers.
            string? destinationAddress = eventArgs.Headers.TryGetValue(HeaderKeys.DestinationAddress, out var daVal)
                ? HeaderDecoder.Decode(daVal) : null;
            string? messageId = eventArgs.Headers.TryGetValue(HeaderKeys.MessageId, out var miVal)
                ? HeaderDecoder.Decode(miVal) : null;
            string? correlationId = eventArgs.Headers.TryGetValue(HeaderKeys.CorrelationId, out var ciVal)
                ? HeaderDecoder.Decode(ciVal) : null;

            activity.DisplayName = Truncate((string.IsNullOrWhiteSpace(destinationAddress) ? "anonymous" : destinationAddress) + " receive", options.MaxTagValueLength);

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
                activity.SetTag(MessagingAttributes.MessagingDestinationAnonymous, "true");
            }

            if (eventArgs.Message is not null)
            {
                activity.SetTag(MessagingAttributes.MessagingBodySize, eventArgs.Message.Length);
                TryEnrich(activity, eventArgs.Message, options);
            }

            return activity;
        }
        catch
        {
            activity.Dispose();
            throw;
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
        ActivityContext linkedContext = default)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(attributes);

        // SendAsync writes to a specific queue (point-to-point), but in OTel messaging
        // semantic conventions that is still classified as "publish" — the producer-side
        // operation name. The point-to-point distinction is preserved by the shared
        // _activitySource and the per-destination DisplayName ("<queue> send"), so
        // backends that need to disaggregate send from publish can filter by activity name.
        Activity? activity = StartActivityWithLink(
            _activitySource,
            ActivitySourceName,
            ActivityKind.Producer,
            options.EnableSendTelemetry,
            attributes,
            "publish",
            linkedContext);

        if (activity is null)
        {
            // L13: single inject. No activity → propagate ambient context for downstream linking.
            InjectTraceContext(Activity.Current, eventArgs.Headers);
            return null;
        }

        try
        {
            // L13: single inject. Activity non-null → propagate the new span's context.
            InjectTraceContext(activity, eventArgs.Headers);

            // Compute the effective destination from EndPoint (singular) first, then fall back
            // to EndPoints (plural, comma-joined). Preserves single-endpoint display while surfacing
            // multi-destination fan-outs that would otherwise appear as anonymous sends in traces.
            // Whitespace entries are filtered before joining so a stray ""/null slot cannot leak into
            // traces as "queue-a,,queue-b"; if filtering empties the list, fall through to anonymous.
            string? destination;
            if (!string.IsNullOrWhiteSpace(eventArgs.EndPoint))
            {
                destination = eventArgs.EndPoint;
            }
            else if (eventArgs.EndPoints.Count > 0)
            {
                var nonEmpty = eventArgs.EndPoints.Where(e => !string.IsNullOrWhiteSpace(e));
                var joined = string.Join(",", nonEmpty);
                destination = joined.Length > 0 ? joined : null;
            }
            else
            {
                destination = null;
            }

            activity.DisplayName = Truncate((destination ?? "anonymous") + " send", options.MaxTagValueLength);

            if (destination is not null)
            {
                activity.SetTag(MessagingAttributes.MessagingDestination,
                    Truncate(destination, options.MaxTagValueLength));
            }
            else
            {
                activity.SetTag(MessagingAttributes.MessagingDestinationAnonymous, "true");
            }

            if (eventArgs.Message is null)
            {
                return activity;
            }

            activity.SetTag(MessagingAttributes.MessageConversationId,
                Truncate(eventArgs.Message.CorrelationId.ToString(), options.MaxTagValueLength));

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
            activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                ["exception.type"] = exception.GetType().FullName,
                ["exception.message"] = message,
                ["exception.stacktrace"] = exception.ToString(),
            }));
        }
#else
        // .NET 8 fallback: record the OTel semantic-convention "exception" event manually.
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.type"] = exception.GetType().FullName,
            ["exception.message"] = message,
            ["exception.stacktrace"] = exception.ToString(),
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
    private static void InjectTraceContext(Activity? activity, IDictionary<string, string> headers)
    {
        if (activity is null)
        {
            return;
        }

        DistributedContextPropagator.Current.Inject(activity, headers, InjectHeader);
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

    private static string Truncate(string? value, int maxLength)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (maxLength <= 0 || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
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
        ActivityContext parentContext)
    {
        if (!enabled || !activitySource.HasListeners())
        {
            return null;
        }

        Activity? activity = activitySource.StartActivity(activityName, kind, parentContext);
        if (activity is null)
        {
            return null;
        }

        activity
            .SetTag(MessagingAttributes.MessagingSystem, attributes.MessagingSystem)
            .SetTag(MessagingAttributes.ProtocolName, attributes.ProtocolName)
            .SetTag(MessagingAttributes.MessagingOperation, operation);

        return activity;
    }

    private static Activity? StartActivityWithLink(
        ActivitySource activitySource,
        string activityName,
        ActivityKind kind,
        bool enabled,
        IMessagingSystemAttributes attributes,
        string operation,
        ActivityContext linkedContext)
    {
        if (!enabled || !activitySource.HasListeners())
        {
            return null;
        }

        // L11: pass linkedContext as the parent so the activity is properly parented.
        // When linkedContext is default, ActivitySource falls back to Activity.Current
        // which is the desired ambient behaviour.
        Activity? activity = activitySource.StartActivity(activityName, kind, linkedContext);
        if (activity is null)
        {
            return null;
        }

        activity
            .SetTag(MessagingAttributes.MessagingSystem, attributes.MessagingSystem)
            .SetTag(MessagingAttributes.ProtocolName, attributes.ProtocolName)
            .SetTag(MessagingAttributes.MessagingOperation, operation);

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
