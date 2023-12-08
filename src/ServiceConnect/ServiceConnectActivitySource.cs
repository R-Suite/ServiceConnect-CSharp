using ServiceConnect.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace ServiceConnect;

public static class ServiceConnectActivitySource
{
    public static ServiceConnectInstrumentationOptions Options { get; set; } = new();

    internal static readonly Version Version = typeof(ServiceConnectActivitySource).Assembly.GetName().Version;
    internal static readonly string ActivitySourceName = typeof(ServiceConnectActivitySource).Assembly.GetName().Name + ".Bus" ?? "ServiceConnect.Bus";

    public static readonly string PublishActivitySourceName = ActivitySourceName + ".Publish";
    public static readonly string ConsumeActivitySourceName = ActivitySourceName + ".Consume";
    public static readonly string SendActivitySourceName = ActivitySourceName + ".Send";

    private static readonly ActivitySource _publishActivitySource = new(PublishActivitySourceName, Version?.ToString() ?? "0.0.0");
    private static readonly ActivitySource _consumeActivitySource = new(ConsumeActivitySourceName, Version?.ToString() ?? "0.0.0");
    private static readonly ActivitySource _sendActivitySource = new(SendActivitySourceName, Version?.ToString() ?? "0.0.0");

    public static Activity StartPublishActivity(PublishEventArgs eventArgs, ActivityContext linkedContext = default)
    {
        if (!_publishActivitySource.HasListeners())
        {
            return null;
        }

        Activity activity = _publishActivitySource.StartActivity(PublishActivitySourceName, ActivityKind.Producer, linkedContext);

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

        if (eventArgs.Headers.TryGetValue("MessageId", out string messageId) && messageId is not null)
        {
            activity.SetTag(MessagingAttributes.MessageId, messageId);
        }

        if (eventArgs.Message is not null && Options.EnrichWithMessage is not null)
        {
            try
            {
                Options.EnrichWithMessage.Invoke(activity, eventArgs.Message);
            }
            catch (Exception ex)
            {
                activity.SetTag("enrichment.exception", ex.Message);
            }
        }

        return activity;
    }

    public static Activity StartConsumeActivity(ConsumeEventArgs eventArgs)
    {
        if (!_consumeActivitySource.HasListeners())
        {
            return null;
        }

        DistributedContextPropagator.Current.ExtractTraceIdAndState(eventArgs.Headers, ExtractTraceIdAndState, out string traceId, out string traceState);
        ActivityContext.TryParse(traceId, traceState, out ActivityContext parentContext);

        Activity activity = _consumeActivitySource.StartActivity(ConsumeActivitySourceName, ActivityKind.Consumer, parentContext);

        if (activity is null)
        {
            return null;
        }

        activity
            .SetTag(MessagingAttributes.MessagingSystem, "rabbitmq")
            .SetTag(MessagingAttributes.ProtocolName, "amqp")
            .SetTag(MessagingAttributes.MessagingOperation, "receive");

        Dictionary<string, string> readableHeaders = new();
        foreach (var kvp in eventArgs.Headers.ToList())
        {
            if (kvp.Value.GetType() == typeof(byte[]))
            {
                readableHeaders[kvp.Key] = Encoding.UTF8.GetString((byte[])kvp.Value);
                continue;
            }

            readableHeaders[kvp.Key] = kvp.Value.ToString();
        }

        readableHeaders.TryGetValue("DestinationAddress", out string destinationAddress);
        activity.DisplayName = (string.IsNullOrWhiteSpace(destinationAddress) ? "anonymous" : destinationAddress) + " receive";

        if (readableHeaders.TryGetValue("MessageId", out string messageId) && messageId is not null)
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

            if (Options.EnrichWithMessageBytes is not null)
            {
                try
                {
                    Options.EnrichWithMessageBytes.Invoke(activity, eventArgs.Message);
                }
                catch (Exception ex)
                {
                    activity.SetTag("enrichment.exception", ex.Message);
                }
            }
        }

        return activity;
    }

    public static Activity StartSendAcitivty(SendEventArgs eventArgs, ActivityContext linkedContext = default)
    {
        if (!_sendActivitySource.HasListeners())
        {
            return null;
        }

        Activity activity = _sendActivitySource.StartActivity(SendActivitySourceName, ActivityKind.Producer, linkedContext);

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

        if (Options.EnrichWithMessage is not null)
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

    public static bool TryGetExistingContext(Dictionary<string, string> headers, out ActivityContext context)
    {
        if (headers == null)
        {
            context = default;
            return false;
        }

        bool hasHeaders = DistributedContextPropagator.Current.Fields.Any(header => headers.ContainsKey(header));

        if (hasHeaders)
        {
            DistributedContextPropagator.Current.ExtractTraceIdAndState(headers, ExtractTraceIdAndState,
                out string traceParent, out string traceState);
            return ActivityContext.TryParse(traceParent, traceState, out context);
        }

        context = default;
        return false;
    }

    private static void ExtractTraceIdAndState(object eventArgs, string name, out string value, out IEnumerable<string> values)
    {
        if (eventArgs is Dictionary<string, object> headers && headers.TryGetValue(name, out object propsVal))
        {
            if (propsVal is byte[] bytes)
            {
                value = Encoding.UTF8.GetString(bytes);
                values = default;
                return;
            }
            if (propsVal is string stringValue)
            {
                value = stringValue;
                values = default;
                return;
            }
        }

        value = default;
        values = default;
    }
}