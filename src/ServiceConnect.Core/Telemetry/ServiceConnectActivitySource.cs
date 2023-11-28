using System;
using System.Diagnostics;

namespace ServiceConnect.Core.Telemetry;

public static class ServiceConnectActivitySource
{
    public static ServiceConnectInstrumentationOptions Options { get; set; } = new();

    internal static readonly Version Version = typeof(ServiceConnectActivitySource).Assembly.GetName().Version;
    internal static readonly string ActivitySourceName = typeof(ServiceConnectActivitySource).Assembly.GetName().Name ?? "ServiceConnect";
    internal static readonly string ActivityName = ActivitySourceName + ".Bus";
    private static readonly ActivitySource _publishActivitySource = new(ActivitySourceName, Version?.ToString() ?? "0.0.0");

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
}