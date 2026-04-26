using System.Diagnostics;
using System.Text;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;
using Xunit;
using static ServiceConnect.Telemetry.MessagingAttributes;

namespace ServiceConnect.UnitTests.Telemetry;

[CollectionDefinition("ActivityListener", DisableParallelization = true)]
public class ActivityListenerCollection { }

[Collection("ActivityListener")]
public sealed class ServiceConnectActivitySourceTests : IDisposable
{
    private readonly ActivityListener _listener;

    public ServiceConnectActivitySourceTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src =>
                src.Name == ServiceConnectActivitySource.PublishActivitySourceName
                || src.Name == ServiceConnectActivitySource.ConsumeActivitySourceName
                || src.Name == ServiceConnectActivitySource.SendActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        // Reset user-configurable enrichers in case a test set them.
        ServiceConnectActivitySource.Options.EnrichWithMessage = null;
        ServiceConnectActivitySource.Options.EnrichWithMessageBytes = null;
        ServiceConnectActivitySource.Options.EnablePublishTelemetry = true;
        ServiceConnectActivitySource.Options.EnableConsumeTelemetry = true;
        ServiceConnectActivitySource.Options.EnableSendTelemetry = true;
        _listener.Dispose();
    }

    // ---------------- Publish ----------------

    [Fact]
    public void Publish_WithExchange_SetsNamedDestinationAndDisplayName()
    {
        var args = new PublishEventArgs
        {
            Exchange = "orders",
            Message = new Message(Guid.NewGuid()),
            Headers = { ["MessageId"] = "msg-1" }
        };

        using var activity = ServiceConnectActivitySource.Publish(args);

        Assert.NotNull(activity);
        Assert.Equal("orders publish", activity!.DisplayName);
        Assert.Equal("orders", activity.GetTagItem(MessagingDestination));
        Assert.Equal("rabbitmq", activity.GetTagItem(MessagingSystem));
        Assert.Equal("publish", activity.GetTagItem(MessagingOperation));
        Assert.Equal("msg-1", activity.GetTagItem(MessageId));
    }

    [Fact]
    public void Publish_WithExchangeAndRoutingKey_StampsExchangeAsDestinationAndRoutingKeySeparately()
    {
        // OTel messaging semconv (RabbitMQ): messaging.destination.name carries the exchange
        // name; messaging.rabbitmq.destination.routing_key carries the routing key. Stamping
        // the routing key onto both attributes broke dashboards keyed on destination.
        var args = new PublishEventArgs
        {
            Exchange = "OrderPlaced",
            RoutingKey = "high-priority",
            Message = new Message(Guid.NewGuid()),
            Headers = { ["MessageId"] = "msg-1" }
        };

        using var activity = ServiceConnectActivitySource.Publish(args);

        Assert.NotNull(activity);
        Assert.Equal("OrderPlaced publish", activity!.DisplayName);
        Assert.Equal("OrderPlaced", activity.GetTagItem(MessagingDestination));
        Assert.Equal("high-priority", activity.GetTagItem(MessagingDestinationRoutingKey));
        // Regression guard: the bug being fixed stamped the routing key onto the destination tag.
        // Asserting the exchange value above implicitly catches it, but the contrived case where
        // exchange == routing-key would mask a regression. Make the negative assertion explicit.
        Assert.NotEqual("high-priority", activity.GetTagItem(MessagingDestination));
    }

    [Fact]
    public void Publish_WithExchangeOnlyAndEmptyRoutingKey_OmitsRoutingKeyTag()
    {
        // RabbitMQ fanout publish — exchange is the type-derived name and routing key is empty.
        // The destination-name tag must be the exchange; the routing-key tag must not be set.
        var args = new PublishEventArgs
        {
            Exchange = "OrderPlaced",
            RoutingKey = "",
            Message = new Message(Guid.NewGuid()),
        };

        using var activity = ServiceConnectActivitySource.Publish(args);

        Assert.NotNull(activity);
        Assert.Equal("OrderPlaced publish", activity!.DisplayName);
        Assert.Equal("OrderPlaced", activity.GetTagItem(MessagingDestination));
        Assert.Null(activity.GetTagItem(MessagingDestinationRoutingKey));
    }

    [Fact]
    public void Publish_WithEmptyExchange_MarksDestinationAnonymous()
    {
        var args = new PublishEventArgs
        {
            Exchange = "",
            RoutingKey = "",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Publish(args);

        Assert.NotNull(activity);
        Assert.Equal("anonymous publish", activity!.DisplayName);
        Assert.Equal("true", activity.GetTagItem(MessagingDestinationAnonymous));
        Assert.Null(activity.GetTagItem(MessagingDestination));
    }

    [Fact]
    public void Publish_EnricherThrows_RecordsEnrichmentException()
    {
        ServiceConnectActivitySource.Options.EnrichWithMessage =
            (_, _) => throw new InvalidOperationException("boom");

        var args = new PublishEventArgs
        {
            Exchange = "orders",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Publish(args);

        Assert.NotNull(activity);
        Assert.Equal("System.InvalidOperationException", activity!.GetTagItem("enrichment.exception"));
    }

    [Fact]
    public void Publish_WhenTelemetryDisabled_ReturnsNull()
    {
        ServiceConnectActivitySource.Options.EnablePublishTelemetry = false;

        var args = new PublishEventArgs
        {
            Exchange = "orders",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Publish(args);

        Assert.Null(activity);
    }

    [Fact]
    public void Publish_InjectsTraceparent_IntoOutgoingHeaders()
    {
        var args = new PublishEventArgs
        {
            Exchange = "orders",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Publish(args);

        Assert.NotNull(activity);
        Assert.True(args.Headers.TryGetValue("traceparent", out var traceparent));
        // W3C traceparent: 00-<32 hex>-<16 hex>-<2 hex>
        Assert.Matches("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$", traceparent);
        // The injected traceparent must carry the started activity's trace/span ids
        // so downstream consumers link to this publish.
        Assert.Contains(activity!.TraceId.ToString(), traceparent);
        Assert.Contains(activity.SpanId.ToString(), traceparent);
    }

    // Companion: Publish_WhenPublishTelemetryDisabled_StillInjectsTraceparentFromAmbient covers the case where an ambient span IS present.
    [Fact]
    public void Publish_WhenTelemetryDisabled_AndNoAmbientActivity_DoesNotTouchHeaders()
    {
        ServiceConnectActivitySource.Options.EnablePublishTelemetry = false;

        var args = new PublishEventArgs
        {
            Exchange = "orders",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Publish(args);

        Assert.Null(activity);
        Assert.False(args.Headers.ContainsKey("traceparent"));
    }

    // ---------------- Consume ----------------

    [Fact]
    public void Consume_SetsMessagingTags_AndDestinationFromHeader()
    {
        var args = new ConsumeEventArgs
        {
            Message = new byte[] { 1, 2, 3 },
            Headers = new Dictionary<string, object>
            {
                ["DestinationAddress"] = Encoding.UTF8.GetBytes("svc.inbox"),
                ["MessageId"] = Encoding.UTF8.GetBytes("msg-42")
            }
        };

        using var activity = ServiceConnectActivitySource.Consume(args);

        Assert.NotNull(activity);
        Assert.Equal("svc.inbox receive", activity!.DisplayName);
        Assert.Equal("svc.inbox", activity.GetTagItem(MessagingDestination));
        Assert.Equal("msg-42", activity.GetTagItem(MessageId));
        Assert.Equal("rabbitmq", activity.GetTagItem(MessagingSystem));
        Assert.Equal("receive", activity.GetTagItem(MessagingOperation));
        Assert.Equal(3, activity.GetTagItem(MessagingBodySize));
    }

    [Fact]
    public void Consume_WithoutDestinationHeader_MarksAnonymous()
    {
        var args = new ConsumeEventArgs
        {
            Message = Array.Empty<byte>(),
            Headers = new Dictionary<string, object>()
        };

        using var activity = ServiceConnectActivitySource.Consume(args);

        Assert.NotNull(activity);
        Assert.Equal("anonymous receive", activity!.DisplayName);
        Assert.Equal("true", activity.GetTagItem(MessagingDestinationAnonymous));
    }

    [Fact]
    public void Consume_ExtractsParentContext_FromTraceparentHeader()
    {
        // Build a valid W3C traceparent: 00-<32 hex traceId>-<16 hex spanId>-01
        var traceId = "0af7651916cd43dd8448eb211c80319c";
        var spanId = "b7ad6b7169203331";
        var traceparent = $"00-{traceId}-{spanId}-01";

        var args = new ConsumeEventArgs
        {
            Headers = new Dictionary<string, object>
            {
                ["traceparent"] = Encoding.UTF8.GetBytes(traceparent)
            }
        };

        using var activity = ServiceConnectActivitySource.Consume(args);

        Assert.NotNull(activity);
        Assert.Equal(traceId, activity!.TraceId.ToString());
        Assert.Equal(spanId, activity.ParentSpanId.ToString());
    }

    [Fact]
    public void Consume_ExtractsParentContext_WhenHeadersAreReadOnlyDictionary()
    {
        // ExtractTraceIdAndState must accept any IReadOnlyDictionary shape so
        // traceparent/tracestate are picked up even when ConsumeContext.Headers
        // is wrapped as a ReadOnlyDictionary. Otherwise consumes would orphan
        // each span as a new trace root instead of continuing the caller's trace.
        var traceId = "0af7651916cd43dd8448eb211c80319c";
        var spanId = "b7ad6b7169203331";
        var inner = new Dictionary<string, object>
        {
            ["traceparent"] = Encoding.UTF8.GetBytes($"00-{traceId}-{spanId}-01"),
        };

        var args = new ConsumeEventArgs
        {
            Headers = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object>(inner),
        };

        using var activity = ServiceConnectActivitySource.Consume(args);

        Assert.NotNull(activity);
        Assert.Equal(traceId, activity!.TraceId.ToString());
        Assert.Equal(spanId, activity.ParentSpanId.ToString());
    }

    [Fact]
    public void Consume_WhenTelemetryDisabled_ReturnsNull()
    {
        ServiceConnectActivitySource.Options.EnableConsumeTelemetry = false;

        var args = new ConsumeEventArgs
        {
            Headers = new Dictionary<string, object>()
        };

        using var activity = ServiceConnectActivitySource.Consume(args);

        Assert.Null(activity);
    }

    // ---------------- Send ----------------

    [Fact]
    public void Send_WithEndpoint_SetsNamedDestination()
    {
        var args = new SendEventArgs
        {
            EndPoint = "svc.queue",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Send(args);

        Assert.NotNull(activity);
        // The DisplayName carries "send" for per-destination tracing; the messaging.operation
        // tag is "publish" per OTel semconv (producer-side regardless of point-to-point vs pub/sub).
        Assert.Equal("svc.queue send", activity!.DisplayName);
        Assert.Equal("publish", activity.GetTagItem(MessagingOperation));
        Assert.Equal("svc.queue", activity.GetTagItem(MessagingDestination));
    }

    [Fact]
    public void Send_WithoutEndpoint_MarksAnonymous()
    {
        var args = new SendEventArgs
        {
            EndPoint = "",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Send(args);

        Assert.NotNull(activity);
        Assert.Equal("anonymous send", activity!.DisplayName);
        Assert.Equal("true", activity.GetTagItem(MessagingDestinationAnonymous));
    }

    [Fact]
    public void Send_WhenTelemetryDisabled_ReturnsNull()
    {
        ServiceConnectActivitySource.Options.EnableSendTelemetry = false;

        var args = new SendEventArgs
        {
            EndPoint = "svc.queue",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Send(args);

        Assert.Null(activity);
    }

    [Fact]
    public void Send_InjectsTraceparent_IntoOutgoingHeaders()
    {
        var args = new SendEventArgs
        {
            EndPoint = "svc.queue",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Send(args);

        Assert.NotNull(activity);
        Assert.True(args.Headers.TryGetValue("traceparent", out var traceparent));
        Assert.Matches("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$", traceparent);
        Assert.Contains(activity!.TraceId.ToString(), traceparent);
        Assert.Contains(activity.SpanId.ToString(), traceparent);
    }

    [Fact]
    public void Send_WithNullMessage_StillInjectsTraceparentHeader()
    {
        // M19: Send must inject traceparent even when Message is null so that
        // payload-less sends still propagate W3C context across the broker.
        // The previously-asserted behaviour (no injection on null Message) was the bug.
        var args = new SendEventArgs
        {
            EndPoint = "svc.queue",
            Message = null
        };

        using var activity = ServiceConnectActivitySource.Send(args);

        // With an active listener the call starts an activity, making
        // Activity.Current non-null, so traceparent must be present.
        Assert.True(args.Headers.ContainsKey("traceparent"));
    }

    [Fact]
    public void Publish_WhenPublishTelemetryDisabled_StillInjectsTraceparentFromAmbient()
    {
        // M20: An outer (e.g. ASP.NET) ambient activity must propagate across the
        // broker even when ServiceConnect's own Publish spans are disabled.
        ServiceConnectActivitySource.Options.EnablePublishTelemetry = false;

        // The existing listener (set up in the constructor) listens to ServiceConnect
        // sources; we need a separate listener for the ambient "ambient" source.
        using var ambientListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "ambient",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(ambientListener);

        using var outerActivity = new ActivitySource("ambient").StartActivity("outer", ActivityKind.Server);
        Assert.NotNull(outerActivity); // Sanity: outer activity must be non-null to make Activity.Current non-null.

        var args = new PublishEventArgs { Exchange = "orders" };
        using var scActivity = ServiceConnectActivitySource.Publish(args);

        Assert.Null(scActivity); // SC telemetry disabled → no SC span
        Assert.True(args.Headers.ContainsKey("traceparent")); // ambient context must be injected
    }

    [Fact]
    public void Send_WhenSendTelemetryDisabled_StillInjectsTraceparentFromAmbient()
    {
        // M20 (Send variant): symmetric to the Publish variant above.
        ServiceConnectActivitySource.Options.EnableSendTelemetry = false;

        using var ambientListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "ambient-send",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(ambientListener);

        using var outerActivity = new ActivitySource("ambient-send").StartActivity("outer", ActivityKind.Server);
        Assert.NotNull(outerActivity);

        var args = new SendEventArgs { EndPoint = "svc.queue" };
        using var scActivity = ServiceConnectActivitySource.Send(args);

        Assert.Null(scActivity); // SC telemetry disabled → no SC span
        Assert.True(args.Headers.ContainsKey("traceparent")); // ambient context must be injected
    }

    // ---------------- SetError (M22) ----------------

    [Fact]
    public void SetError_SetsActivityStatusToError_AndRecordsExceptionDetails()
    {
        // M22: SetError must mark the activity as Error and attach exception metadata
        // so OTel backends surface it in error-rate dashboards.
        // AddException records details as an ActivityEvent named "exception", not as
        // activity-level tags, which is why we inspect Events rather than GetTagItem.
        var args = new PublishEventArgs { Exchange = "orders", Message = new Message(Guid.NewGuid()) };
        using var activity = ServiceConnectActivitySource.Publish(args);
        Assert.NotNull(activity);

        var ex = new InvalidOperationException("publish failed");
        ServiceConnectActivitySource.SetError(activity, ex);

        Assert.Equal(ActivityStatusCode.Error, activity!.Status);
        // SetStatus(Error, message) populates StatusDescription with the exception message.
        Assert.Equal(ex.Message, activity.StatusDescription);

        var exceptionEvent = activity.Events.FirstOrDefault(e => e.Name == "exception");
        Assert.NotEqual(default, exceptionEvent);
        var exTypeTag = exceptionEvent.Tags.FirstOrDefault(t => t.Key == "exception.type").Value?.ToString();
        Assert.Equal(typeof(InvalidOperationException).FullName, exTypeTag);
        // exception.message must match so error-rate dashboards show the right message.
        var exMessageTag = exceptionEvent.Tags.FirstOrDefault(t => t.Key == "exception.message").Value?.ToString();
        Assert.Equal(ex.Message, exMessageTag);
        // exception.stacktrace must be present (exact format is BCL-defined, so only check non-null).
        var exStackTag = exceptionEvent.Tags.FirstOrDefault(t => t.Key == "exception.stacktrace").Value?.ToString();
        Assert.NotNull(exStackTag);
    }

    [Fact]
    public void SetError_WithNullActivity_IsNoOp()
    {
        // SetError must not throw when called with a null activity (e.g. telemetry disabled).
        var ex = new InvalidOperationException("oops");
        var exception = Record.Exception(() => ServiceConnectActivitySource.SetError(null, ex));
        Assert.Null(exception);
    }

    // ---------------- TryGetExistingContext ----------------

    [Fact]
    public void TryGetExistingContext_WithTraceparent_ReturnsTrue_AndParsesContext()
    {
        var traceId = "0af7651916cd43dd8448eb211c80319c";
        var spanId = "b7ad6b7169203331";
        var headers = new Dictionary<string, string>
        {
            ["traceparent"] = $"00-{traceId}-{spanId}-01"
        };

        var ok = ServiceConnectActivitySource.TryGetExistingContext(headers, out var ctx);

        Assert.True(ok);
        Assert.Equal(traceId, ctx.TraceId.ToString());
        Assert.Equal(spanId, ctx.SpanId.ToString());
    }

    [Fact]
    public void TryGetExistingContext_WithNullHeaders_ReturnsFalse()
    {
        var ok = ServiceConnectActivitySource.TryGetExistingContext(null!, out var ctx);

        Assert.False(ok);
        Assert.Equal(default, ctx);
    }

    [Fact]
    public void TryGetExistingContext_WithoutTraceHeaders_ReturnsFalse()
    {
        var headers = new Dictionary<string, string> { ["Unrelated"] = "v" };

        var ok = ServiceConnectActivitySource.TryGetExistingContext(headers, out var ctx);

        Assert.False(ok);
        Assert.Equal(default, ctx);
    }

    [Fact]
    public void Send_EndPointsPluralOnly_TagsJoinedDestination()
    {
        // L14: Multi-destination sends lost their destination in telemetry because only
        // the singular EndPoint was ever read. Verify EndPoints (plural) surfaces as a
        // comma-joined messaging.destination tag and DisplayName.
        var args = new SendEventArgs
        {
            EndPoint = "",
            EndPoints = new[] { "queue-a", "queue-b" },
            Headers = new Dictionary<string, string>(),
        };

        using var activity = ServiceConnectActivitySource.Send(args);

        Assert.NotNull(activity);
        Assert.Equal("queue-a,queue-b send", activity!.DisplayName);
        Assert.Equal("queue-a,queue-b", activity.GetTagItem(MessagingDestination));
    }

    [Fact]
    public void Send_EndPointsContainsWhitespaceEntries_FiltersBeforeJoining()
    {
        // Holistic follow-up: a stray ""/null/whitespace entry in EndPoints must not leak
        // into traces as "queue-a,,queue-b". Confirm whitespace entries drop out of the
        // joined destination tag and display name.
        var args = new SendEventArgs
        {
            EndPoint = "",
            EndPoints = new[] { "queue-a", "", "   ", "queue-b" },
            Headers = new Dictionary<string, string>(),
        };

        using var activity = ServiceConnectActivitySource.Send(args);

        Assert.NotNull(activity);
        Assert.Equal("queue-a,queue-b send", activity!.DisplayName);
        Assert.Equal("queue-a,queue-b", activity.GetTagItem(MessagingDestination));
    }

    [Fact]
    public void Send_EndPointsAllWhitespace_FallsBackToAnonymous()
    {
        // Holistic follow-up: if filtering empties the list, the send must be tagged
        // anonymous rather than producing a spurious empty-string destination.
        var args = new SendEventArgs
        {
            EndPoint = "",
            EndPoints = new[] { "", "   " },
            Headers = new Dictionary<string, string>(),
        };

        using var activity = ServiceConnectActivitySource.Send(args);

        Assert.NotNull(activity);
        Assert.Equal("anonymous send", activity!.DisplayName);
        Assert.Null(activity.GetTagItem(MessagingDestination));
        Assert.Equal("true", activity.GetTagItem(MessagingDestinationAnonymous));
    }

    [Fact]
    public void Send_SetsMessagingOperation_ToPublish()
    {
        var args = new SendEventArgs
        {
            EndPoint = "queue-a",
            Headers = new Dictionary<string, string>(),
            Message = null,
        };

        using var activity = ServiceConnectActivitySource.Send(args);

        Assert.NotNull(activity);
        var operation = activity!.GetTagItem(MessagingOperation);
        Assert.Equal("publish", operation);
    }
}

[Collection("ActivityListener")]
public sealed class ServiceConnectActivitySource_NoListenerTests
{
    [Fact]
    public void Publish_ReturnsNull_WhenNoListeners()
    {
        var args = new PublishEventArgs
        {
            Exchange = "orders",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Publish(args);

        Assert.Null(activity);
    }

    [Fact]
    public void Consume_ReturnsNull_WhenNoListeners()
    {
        var args = new ConsumeEventArgs();

        using var activity = ServiceConnectActivitySource.Consume(args);

        Assert.Null(activity);
    }

    [Fact]
    public void Send_ReturnsNull_WhenNoListeners()
    {
        var args = new SendEventArgs { EndPoint = "ep" };

        using var activity = ServiceConnectActivitySource.Send(args);

        Assert.Null(activity);
    }
}
