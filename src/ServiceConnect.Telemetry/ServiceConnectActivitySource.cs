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
        Activity? activity = StartActivity(
            _publishActivitySource,
            PublishActivitySourceName,
            ActivityKind.Producer,
            Options.EnablePublishTelemetry,
            "publish",
            linkedContext);

        if (activity is null) return null;

        activity.SetTag(MessagingAttributes.MessageConversationId, eventArgs.Message?.CorrelationId.ToString());

        if (!string.IsNullOrWhiteSpace(eventArgs.RoutingKey))
        {
            activity.DisplayName = eventArgs.RoutingKey + " publish";
            activity
                .SetTag(MessagingAttributes.MessagingDestination, eventArgs.RoutingKey)
                .SetTag(MessagingAttributes.MessagingDestinationRoutingKey, eventArgs.RoutingKey);
        }
        else
        {
            activity.DisplayName = "anonymous publish";
            activity.SetTag(MessagingAttributes.MessagingDestinationAnonymous, "true");
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

        Activity? activity = StartActivity(
            _consumeActivitySource,
            ConsumeActivitySourceName,
            ActivityKind.Consumer,
            Options.EnableConsumeTelemetry,
            "receive",
            parentContext);

        if (activity is null) return null;

        // Targeted header lookups — decode only the two headers actually used here
        // rather than allocating a full decode dictionary for all 15-20 headers.
        string? destinationAddress = eventArgs.Headers.TryGetValue(HeaderKeys.DestinationAddress, out var daVal)
            ? HeaderDecoder.Decode(daVal) : null;
        string? messageId = eventArgs.Headers.TryGetValue(HeaderKeys.MessageId, out var miVal)
            ? HeaderDecoder.Decode(miVal) : null;

        activity.DisplayName = (string.IsNullOrWhiteSpace(destinationAddress) ? "anonymous" : destinationAddress) + " receive";

        if (messageId is not null)
        {
            activity.SetTag(MessagingAttributes.MessageId, messageId);
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
        Activity? activity = StartActivity(
            _sendActivitySource,
            SendActivitySourceName,
            ActivityKind.Producer,
            Options.EnableSendTelemetry,
            "publish",
            linkedContext);

        if (activity is null) return null;

        activity.DisplayName = (string.IsNullOrWhiteSpace(eventArgs.EndPoint) ? "anonymous" : eventArgs.EndPoint) + " publish";

        if (!string.IsNullOrEmpty(eventArgs.EndPoint))
        {
            activity.SetTag(MessagingAttributes.MessagingDestination, eventArgs.EndPoint);
        }
        else
        {
            activity.SetTag(MessagingAttributes.MessagingDestinationAnonymous, "true");
        }

        if (eventArgs.Message is null)
        {
            return activity;
        }

        activity.SetTag(MessagingAttributes.MessageConversationId, eventArgs.Message.CorrelationId.ToString());

        InjectTraceContext(activity, eventArgs.Headers);

        TryEnrich(activity, eventArgs.Message);

        return activity;
    }

    /// <summary>
    /// Attempts to parse a W3C trace context from the supplied headers. Returns
    /// <c>true</c> and populates <paramref name="context"/> when the headers contain
    /// a well-formed traceparent; otherwise returns <c>false</c>.
    /// </summary>
    public static bool TryGetExistingContext(Dictionary<string, string> headers, out ActivityContext context)
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
        switch (eventArgs)
        {
            case Dictionary<string, object> objHeaders when objHeaders.TryGetValue(name, out object? objVal):
                value = HeaderDecoder.Decode(objVal);
                return;
            // string branch: values are already decoded; HeaderDecoder.Decode is for byte[] RabbitMQ headers only.
            case Dictionary<string, string> strHeaders when strHeaders.TryGetValue(name, out string? strVal):
                value = strVal;
                return;
            default:
                value = default;
                return;
        }
    }

    /// <summary>
    /// Writes the current activity's W3C trace context into the outgoing-headers dictionary
    /// so downstream consumers can link their consume span to the originating publish. Mirrors
    /// <see cref="Consume"/>'s extract side — without this, every consume span was a new root
    /// and the end-to-end trace graph was broken outbound.
    /// </summary>
    private static void InjectTraceContext(Activity? activity, Dictionary<string, string> headers)
    {
        if (activity is null) return;
        DistributedContextPropagator.Current.Inject(activity, headers, InjectHeader);
    }

    private static void InjectHeader(object? carrier, string fieldName, string fieldValue)
    {
        if (carrier is Dictionary<string, string> headers)
            headers[fieldName] = fieldValue;
    }

    private static Activity? StartActivity(
        ActivitySource activitySource,
        string activityName,
        ActivityKind kind,
        bool enabled,
        string operation,
        ActivityContext context = default)
    {
        if (!enabled || !activitySource.HasListeners())
            return null;

        Activity? activity = activitySource.StartActivity(activityName, kind, context);
        if (activity is null)
            return null;

        activity
            .SetTag(MessagingAttributes.MessagingSystem, MessagingSystemAttributes.MessagingSystem)
            .SetTag(MessagingAttributes.ProtocolName, MessagingSystemAttributes.ProtocolName)
            .SetTag(MessagingAttributes.MessagingOperation, operation);

        return activity;
    }

    private static void TryEnrich(Activity activity, Message? message)
    {
        if (message is null)
            return;

        try
        {
            Options.EnrichWithMessage?.Invoke(activity, message);
        }
        catch (Exception ex)
        {
            activity.SetTag("enrichment.exception", ex.Message);
        }
    }

    private static void TryEnrich(Activity activity, byte[]? message)
    {
        if (message is null)
            return;

        try
        {
            Options.EnrichWithMessageBytes?.Invoke(activity, message);
        }
        catch (Exception ex)
        {
            activity.SetTag("enrichment.exception", ex.Message);
        }
    }
}
