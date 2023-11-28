using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace ServiceConnect.Telemetry;

internal static class ServiceConnectActivitySource
{
    public static ServiceConnectInstrumentationOptions Options { get; set; } = new();

    internal static readonly Version Version = typeof(ServiceConnectActivitySource).Assembly.GetName().Version;
    internal static readonly string ActivitySourceName = typeof(ServiceConnectActivitySource).Assembly.GetName().Name ?? "ServiceConnect";
    internal static readonly string ActivityName = ActivitySourceName + ".Bus";

    private static readonly ActivitySource _publishActivitySource = new(ActivitySourceName + ".Publish", Version?.ToString() ?? "0.0.0");
    private static readonly ActivitySource _consumeActivitySource = new(ActivitySourceName + ".Consume", Version?.ToString() ?? "0.0.0");
    private static readonly ActivitySource _sendActivitySource = new(ActivitySourceName + ".Send", Version?.ToString() ?? "0.0.0");

    public static Activity Publish(PublishEventArgs eventArgs)
    {
        if (!_publishActivitySource.HasListeners())
        {
            return null;
        }

        // TODO: get context

        Activity activity = _publishActivitySource.StartActivity(ActivityName + ".Publish", ActivityKind.Producer, default(ActivityContext));

        if (activity is not null)
        {
            activity.DisplayName = (string.IsNullOrWhiteSpace(eventArgs.RoutingKey) ? "anonymous" : eventArgs.RoutingKey) + " publish";

            activity
                .SetTag(MessagingAttributes.MessagingSystem, "rabbitmq")
                .SetTag(MessagingAttributes.ProtocolName, "amqp")
                .SetTag(MessagingAttributes.MessagingOperation, "publish")
                .SetTag(MessagingAttributes.MessageConversationId, eventArgs.Message?.CorrelationId);

            if (!string.IsNullOrWhiteSpace(eventArgs.RoutingKey))
            {
                activity
                    .SetTag(MessagingAttributes.MessagingDestination, eventArgs.RoutingKey)
                    .SetTag(MessagingAttributes.MessagingDestinationRoutingKey, eventArgs.RoutingKey);
            }
            else
            {
                activity.SetTag(MessagingAttributes.MessagingDestinationAnonymous, "true");
            }

            if (eventArgs.Headers.TryGetValue("MessageId", out string messageId))
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
        }

        return activity;
    }

    public static Activity Consume(ConsumeEventArgs eventArgs)
    {
        if (!_publishActivitySource.HasListeners())
        {
            return null;
        }

        // TODO: get context

        Activity activity = _consumeActivitySource.StartActivity(ActivityName + ".Consume", ActivityKind.Consumer, default(ActivityContext));

        if (activity is not null)
        {
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
            activity.DisplayName = (destinationAddress ?? "anonymous") + " receive";

            if (readableHeaders.TryGetValue("MessageId", out string messageId))
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
        }

        return activity;
    }

    public static Activity Send(SendEventArgs eventArgs)
    {
        if (!_publishActivitySource.HasListeners())
        {
            return null;
        }

        // TODO: get context

        Activity activity = _sendActivitySource.StartActivity(ActivityName + ".Send", ActivityKind.Producer, default(ActivityContext));

        if (activity is null)
        {
            return null;
        }

        activity
            .SetTag(MessagingAttributes.MessagingSystem, "rabbitmq")
            .SetTag(MessagingAttributes.ProtocolName, "amqp")
            .SetTag(MessagingAttributes.MessagingOperation, "publish");

        activity.DisplayName = (string.IsNullOrWhiteSpace(eventArgs.EndPoint) ? "anonymous" : eventArgs.EndPoint) + " send";

        if (!string.IsNullOrEmpty(eventArgs.EndPoint))
        {
            _ = activity.SetTag(MessagingAttributes.MessagingDestination, eventArgs.EndPoint);
        }
        else
        {
            _ = activity.SetTag(MessagingAttributes.MessagingDestinationAnonymous, "true");
        }

        if (eventArgs.Message is null)
        {
            return activity;
        }

        activity.SetTag(MessagingAttributes.MessageConversationId, eventArgs.Message.CorrelationId);

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
}