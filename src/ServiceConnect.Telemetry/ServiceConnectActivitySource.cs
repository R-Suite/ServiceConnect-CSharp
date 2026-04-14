using System.Diagnostics;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Telemetry;

// Telemetry uses the ServiceConnect.Interfaces event-args types (A-04).

public static class ServiceConnectActivitySource
{
    public static ServiceConnectInstrumentationOptions Options { get; internal set; } = new();

    internal static readonly Version? Version = typeof(ServiceConnectActivitySource).Assembly.GetName().Version;
    internal static readonly string ActivitySourceName = (typeof(ServiceConnectActivitySource).Assembly.GetName().Name ?? "ServiceConnect") + ".Bus";

    public static readonly string PublishActivitySourceName = ActivitySourceName + ".Publish";
    public static readonly string ConsumeActivitySourceName = ActivitySourceName + ".Consume";
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
        if (!_publishActivitySource.HasListeners())
        {
            return null;
        }

        Activity? activity = _publishActivitySource.StartActivity(PublishActivitySourceName, ActivityKind.Producer, linkedContext);

        if (activity is null)
        {
            return null;
        }

        activity
            .SetTag(MessagingAttributes.MessagingSystem, "rabbitmq")
            .SetTag(MessagingAttributes.ProtocolName, "amqp")
            .SetTag(MessagingAttributes.MessagingOperation, "publish")
            .SetTag(MessagingAttributes.MessageConversationId, eventArgs.Message?.CorrelationId.ToString());

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

        if (eventArgs.Message is not null)
        {
            try
            {
                Options.EnrichWithMessage?.Invoke(activity, eventArgs.Message);
            }
            catch (Exception ex)
            {
                activity.SetTag("enrichment.exception", ex.Message);
            }
        }

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
        if (!_consumeActivitySource.HasListeners())
        {
            return null;
        }

        DistributedContextPropagator.Current.ExtractTraceIdAndState(eventArgs.Headers, ExtractTraceIdAndState, out string? traceId, out string? traceState);
        ActivityContext.TryParse(traceId, traceState, out ActivityContext parentContext);

        Activity? activity = _consumeActivitySource.StartActivity(ConsumeActivitySourceName, ActivityKind.Consumer, parentContext);

        if (activity is null)
        {
            return null;
        }

        activity
            .SetTag(MessagingAttributes.MessagingSystem, "rabbitmq")
            .SetTag(MessagingAttributes.ProtocolName, "amqp")
            .SetTag(MessagingAttributes.MessagingOperation, "receive");

        // Targeted header lookups — decode only the two headers actually used here
        // rather than allocating a full decode dictionary for all 15-20 headers (P-008).
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
            try
            {
                Options.EnrichWithMessageBytes?.Invoke(activity, eventArgs.Message);
            }
            catch (Exception ex)
            {
                activity.SetTag("enrichment.exception", ex.Message);
            }
        }

        return activity;
    }

    /// <summary>
    /// Starts a send-side activity. Returns <c>null</c> when no listeners are
    /// registered for <see cref="SendActivitySourceName"/>.
    /// </summary>
    public static Activity? Send(SendEventArgs eventArgs, ActivityContext linkedContext = default)
    {
        if (!_sendActivitySource.HasListeners())
        {
            return null;
        }

        Activity? activity = _sendActivitySource.StartActivity(SendActivitySourceName, ActivityKind.Producer, linkedContext);

        if (activity is null)
        {
            return null;
        }

        activity
            .SetTag(MessagingAttributes.MessagingSystem, "rabbitmq")
            .SetTag(MessagingAttributes.ProtocolName, "amqp")
            .SetTag(MessagingAttributes.MessagingOperation, "publish");

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

        try
        {
            Options.EnrichWithMessage?.Invoke(activity, eventArgs.Message);
        }
        catch (Exception ex)
        {
            activity.SetTag("enrichment.exception", ex.Message);
        }

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

        bool hasHeaders = false;
        foreach (string header in DistributedContextPropagator.Current.Fields)
        {
            if (headers.ContainsKey(header))
            {
                hasHeaders = true;
                break;
            }
        }

        if (hasHeaders)
        {
            DistributedContextPropagator.Current.ExtractTraceIdAndState(headers, ExtractTraceIdAndState,
                out string? traceParent, out string? traceState);
            return ActivityContext.TryParse(traceParent, traceState, out context);
        }

        context = default;
        return false;
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
}