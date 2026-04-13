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

        // Pre-size the dict and iterate the source directly — ToList() was a defensive
        // copy that allocated a full KeyValuePair list per consumed message (P-64).
        var readableHeaders = new Dictionary<string, string?>(eventArgs.Headers.Count);
        foreach (var kvp in eventArgs.Headers)
        {
            readableHeaders[kvp.Key] = HeaderDecoder.Decode(kvp.Value);
        }

        readableHeaders.TryGetValue(HeaderKeys.DestinationAddress, out string? destinationAddress);
        activity.DisplayName = (string.IsNullOrWhiteSpace(destinationAddress) ? "anonymous" : destinationAddress) + " receive";

        if (readableHeaders.TryGetValue(HeaderKeys.MessageId, out string? messageId))
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