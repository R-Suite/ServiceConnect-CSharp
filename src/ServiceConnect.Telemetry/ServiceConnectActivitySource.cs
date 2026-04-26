using System.Diagnostics;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Telemetry;

// Telemetry uses the ServiceConnect.Interfaces event-args types.

/// <summary>
/// Creates publish, send, and consume activities for ServiceConnect message operations.
/// </summary>
public static class ServiceConnectActivitySource
{
    /// <summary>
    /// Gets or sets the telemetry options that control enrichment and activity enablement.
    /// </summary>
    public static ServiceConnectInstrumentationOptions Options { get; internal set; } = new();
    /// <summary>
    /// Gets or sets the messaging-system semantic-convention values applied to generated activities.
    /// </summary>
    public static IMessagingSystemAttributes MessagingSystemAttributes { get; internal set; } = new RabbitMqMessagingSystemAttributes();

    internal static readonly Version? Version = typeof(ServiceConnectActivitySource).Assembly.GetName().Version;
    internal static readonly string ActivitySourceName = (typeof(ServiceConnectActivitySource).Assembly.GetName().Name ?? "ServiceConnect") + ".Bus";

    /// <summary>
    /// Gets the activity-source name used for publish spans.
    /// </summary>
    public static readonly string PublishActivitySourceName = ActivitySourceName + ".Publish";
    /// <summary>
    /// Gets the activity-source name used for consume spans.
    /// </summary>
    public static readonly string ConsumeActivitySourceName = ActivitySourceName + ".Consume";
    /// <summary>
    /// Gets the activity-source name used for send spans.
    /// </summary>
    public static readonly string SendActivitySourceName = ActivitySourceName + ".Send";

    private static readonly ActivitySource _publishActivitySource = new(PublishActivitySourceName, Version?.ToString() ?? "0.0.0");
    private static readonly ActivitySource _consumeActivitySource = new(ConsumeActivitySourceName, Version?.ToString() ?? "0.0.0");
    private static readonly ActivitySource _sendActivitySource = new(SendActivitySourceName, Version?.ToString() ?? "0.0.0");

    /// <summary>
    /// Starts a publish-side activity. Returns <c>null</c> when no listeners are
    /// registered for <see cref="PublishActivitySourceName"/>.
    /// </summary>
    public static Activity? Publish(PublishEventArgs eventArgs, ActivityContext linkedContext = default)
    {
        // M20: Inject ambient trace context unconditionally so outer (ASP.NET / OTel) spans
        // propagate across the broker even when ServiceConnect's own spans are disabled.
        InjectTraceContext(Activity.Current, eventArgs.Headers);

        Activity? activity = StartActivityWithLink(
            _publishActivitySource,
            PublishActivitySourceName,
            ActivityKind.Producer,
            Options.EnablePublishTelemetry,
            "publish",
            linkedContext);

        if (activity is null)
        {
            return null;
        }

        activity.SetTag(MessagingAttributes.MessageConversationId, eventArgs.Message?.CorrelationId.ToString());

        if (!string.IsNullOrWhiteSpace(eventArgs.Exchange))
        {
            activity.DisplayName = eventArgs.Exchange + " publish";
            activity.SetTag(MessagingAttributes.MessagingDestination, eventArgs.Exchange);
        }
        else
        {
            activity.DisplayName = "anonymous publish";
            activity.SetTag(MessagingAttributes.MessagingDestinationAnonymous, "true");
        }

        if (!string.IsNullOrWhiteSpace(eventArgs.RoutingKey))
        {
            activity.SetTag(MessagingAttributes.MessagingDestinationRoutingKey, eventArgs.RoutingKey);
        }

        if (eventArgs.Headers.TryGetValue(HeaderKeys.MessageId, out string? messageId))
        {
            activity.SetTag(MessagingAttributes.MessageId, messageId);
        }

        InjectTraceContext(activity, eventArgs.Headers);

        TryEnrich(activity, eventArgs.Message);

        return activity;
    }

    /// <summary>
    /// Starts a consume-side activity, extracting the W3C traceparent/tracestate
    /// from <paramref name="eventArgs"/>.Headers so the resulting span is linked to
    /// the publishing activity. Returns <c>null</c> when no listeners are registered
    /// for <see cref="ConsumeActivitySourceName"/>.
    /// </summary>
    public static Activity? Consume(ConsumeEventArgs eventArgs)
    {
        DistributedContextPropagator.Current.ExtractTraceIdAndState(eventArgs.Headers, ExtractTraceIdAndState, out string? traceId, out string? traceState);
        ActivityContext.TryParse(traceId, traceState, out ActivityContext parentContext);

        Activity? activity = StartActivityWithParent(
            _consumeActivitySource,
            ConsumeActivitySourceName,
            ActivityKind.Consumer,
            Options.EnableConsumeTelemetry,
            "receive",
            parentContext);

        if (activity is null)
        {
            return null;
        }

        // Targeted header lookups — decode only the headers actually used here
        // rather than allocating a full decode dictionary for all 15-20 headers.
        string? destinationAddress = eventArgs.Headers.TryGetValue(HeaderKeys.DestinationAddress, out var daVal)
            ? HeaderDecoder.Decode(daVal) : null;
        string? messageId = eventArgs.Headers.TryGetValue(HeaderKeys.MessageId, out var miVal)
            ? HeaderDecoder.Decode(miVal) : null;
        string? correlationId = eventArgs.Headers.TryGetValue(HeaderKeys.CorrelationId, out var ciVal)
            ? HeaderDecoder.Decode(ciVal) : null;

        activity.DisplayName = (string.IsNullOrWhiteSpace(destinationAddress) ? "anonymous" : destinationAddress) + " receive";

        if (messageId is not null)
        {
            activity.SetTag(MessagingAttributes.MessageId, messageId);
        }

        if (correlationId is not null)
        {
            activity.SetTag(MessagingAttributes.MessageConversationId, correlationId);
        }

        if (!string.IsNullOrEmpty(destinationAddress))
        {
            activity.SetTag(MessagingAttributes.MessagingDestination, destinationAddress);
        }
        else
        {
            activity.SetTag(MessagingAttributes.MessagingDestinationAnonymous, "true");
        }

        if (eventArgs.Message is not null)
        {
            activity.SetTag(MessagingAttributes.MessagingBodySize, eventArgs.Message.Length);
            TryEnrich(activity, eventArgs.Message);
        }

        return activity;
    }

    /// <summary>
    /// Starts a send-side activity. Returns <c>null</c> when no listeners are
    /// registered for <see cref="SendActivitySourceName"/>.
    /// </summary>
    public static Activity? Send(SendEventArgs eventArgs, ActivityContext linkedContext = default)
    {
        // M19/M20: Inject ambient trace context unconditionally — before any early-return — so
        // payload-less sends and disabled-telemetry paths still propagate W3C context across
        // the broker. Downstream inject (below) overwrites with the started activity's span
        // when ServiceConnect's own span is available; otherwise the ambient span propagates.
        InjectTraceContext(Activity.Current, eventArgs.Headers);

        // SendAsync writes to a specific queue (point-to-point), but in OTel messaging
        // semantic conventions that is still classified as "publish" — the producer-side
        // operation name. The point-to-point distinction is preserved by the dedicated
        // _sendActivitySource and the per-destination DisplayName ("<queue> send"), so
        // backends that need to disaggregate send from publish can do so by source name.
        Activity? activity = StartActivityWithLink(
            _sendActivitySource,
            SendActivitySourceName,
            ActivityKind.Producer,
            Options.EnableSendTelemetry,
            "publish",
            linkedContext);

        if (activity is null)
        {
            return null;
        }

        // L14: compute the effective destination from EndPoint (singular) first, then fall back
        // to EndPoints (plural, comma-joined). Preserves single-endpoint display while surfacing
        // multi-destination fan-outs that previously appeared as anonymous sends in traces.
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

        activity.DisplayName = (destination ?? "anonymous") + " send";

        if (destination is not null)
        {
            activity.SetTag(MessagingAttributes.MessagingDestination, destination);
        }
        else
        {
            activity.SetTag(MessagingAttributes.MessagingDestinationAnonymous, "true");
        }

        // M19: Inject the started activity's trace context before the null-message early-return
        // so payload-less sends still carry a traceparent header that downstream consumers can link.
        InjectTraceContext(activity, eventArgs.Headers);

        if (eventArgs.Message is null)
        {
            return activity;
        }

        activity.SetTag(MessagingAttributes.MessageConversationId, eventArgs.Message.CorrelationId.ToString());

        TryEnrich(activity, eventArgs.Message);

        return activity;
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
    public static void SetError(Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
#if NET9_0_OR_GREATER
        // AddException is available on .NET 9+; it records the OTel "exception" event.
        activity.AddException(exception);
#else
        // .NET 8 fallback: record the OTel semantic-convention "exception" event manually.
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.type"] = exception.GetType().FullName,
            ["exception.message"] = exception.Message,
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

    private static void InjectHeader(object? carrier, string fieldName, string fieldValue)
    {
        if (carrier is IDictionary<string, string> headers)
        {
            headers[fieldName] = fieldValue;
        }
    }

    private static Activity? StartActivityWithParent(
        ActivitySource activitySource,
        string activityName,
        ActivityKind kind,
        bool enabled,
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
            .SetTag(MessagingAttributes.MessagingSystem, MessagingSystemAttributes.MessagingSystem)
            .SetTag(MessagingAttributes.ProtocolName, MessagingSystemAttributes.ProtocolName)
            .SetTag(MessagingAttributes.MessagingOperation, operation);

        return activity;
    }

    private static Activity? StartActivityWithLink(
        ActivitySource activitySource,
        string activityName,
        ActivityKind kind,
        bool enabled,
        string operation,
        ActivityContext linkedContext)
    {
        if (!enabled || !activitySource.HasListeners())
        {
            return null;
        }

        ActivityLink[]? links = linkedContext == default
            ? null
            : [new ActivityLink(linkedContext)];

        Activity? activity = activitySource.StartActivity(
            activityName, kind, parentContext: default, tags: null, links: links);
        if (activity is null)
        {
            return null;
        }

        activity
            .SetTag(MessagingAttributes.MessagingSystem, MessagingSystemAttributes.MessagingSystem)
            .SetTag(MessagingAttributes.ProtocolName, MessagingSystemAttributes.ProtocolName)
            .SetTag(MessagingAttributes.MessagingOperation, operation);

        return activity;
    }

    private static void TryEnrich(Activity activity, Message? message)
    {
        if (message is null)
        {
            return;
        }

        try
        {
            Options.EnrichWithMessage?.Invoke(activity, message);
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

    private static void TryEnrich(Activity activity, byte[]? message)
    {
        if (message is null)
        {
            return;
        }

        try
        {
            Options.EnrichWithMessageBytes?.Invoke(activity, message);
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

    internal static void InvokeTryEnrichForTest(Activity activity, Message? message) =>
        TryEnrich(activity, message);

    internal static void InvokeTryEnrichForTest(Activity activity, byte[]? bytes) =>
        TryEnrich(activity, bytes);
}
