using System.Diagnostics;
using System.Text;

namespace ServiceConnect.Telemetry;

public static class ServiceConnectActivitySource
{
    public static ServiceConnectInstrumentationOptions Options { get; set; } = new();

    internal static readonly Version? Version = typeof(ServiceConnectActivitySource).Assembly.GetName().Version;
    internal static readonly string ActivitySourceName = typeof(ServiceConnectActivitySource).Assembly.GetName().Name + ".Bus" ?? "ServiceConnect.Bus";

    public static readonly string PublishActivitySourceName = ActivitySourceName + ".Publish";
    public static readonly string ConsumeActivitySourceName = ActivitySourceName + ".Consume";
    public static readonly string SendActivitySourceName = ActivitySourceName + ".Send";

    private static readonly ActivitySource _publishActivitySource = new(PublishActivitySourceName, Version?.ToString() ?? "0.0.0");
    private static readonly ActivitySource _consumeActivitySource = new(ConsumeActivitySourceName, Version?.ToString() ?? "0.0.0");
    private static readonly ActivitySource _sendActivitySource = new(SendActivitySourceName, Version?.ToString() ?? "0.0.0");

    public static Activity? Publish(PublishEventArgs eventArgs)
    {
        if (!_publishActivitySource.HasListeners())
        {
            return null;
        }

        // TODO: get context

        Activity? activity = _publishActivitySource.StartActivity(PublishActivitySourceName, ActivityKind.Producer, default(ActivityContext));

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

        if (eventArgs.Headers.TryGetValue("MessageId", out string? messageId))
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
        if (!_publishActivitySource.HasListeners())
        {
            return null;
        }

        // TODO: get context

        Activity? activity = _consumeActivitySource.StartActivity(ConsumeActivitySourceName, ActivityKind.Consumer, default(ActivityContext));

        if (activity is not null)
        {
            activity
                .SetTag(MessagingAttributes.MessagingSystem, "rabbitmq")
                .SetTag(MessagingAttributes.ProtocolName, "amqp")
                .SetTag(MessagingAttributes.MessagingOperation, "receive");

            Dictionary<string, string?> readableHeaders = new();
            foreach (var kvp in eventArgs.Headers.ToList())
            {
                if (kvp.Value.GetType() == typeof(byte[]))
                {
                    readableHeaders[kvp.Key] = Encoding.UTF8.GetString((byte[])kvp.Value);
                    continue;
                }

                readableHeaders[kvp.Key] = kvp.Value.ToString();
            }

            readableHeaders.TryGetValue("DestinationAddress", out string? destinationAddress);
            activity.DisplayName = (string.IsNullOrWhiteSpace(destinationAddress) ? "anonymous" : destinationAddress) + " receive";

            if (readableHeaders.TryGetValue("MessageId", out string? messageId))
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

    public static Activity? Send(SendEventArgs eventArgs)
    {
        if (!_publishActivitySource.HasListeners())
        {
            return null;
        }

        // TODO: get context

        Activity? activity = _sendActivitySource.StartActivity(SendActivitySourceName, ActivityKind.Producer, default(ActivityContext));

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
}