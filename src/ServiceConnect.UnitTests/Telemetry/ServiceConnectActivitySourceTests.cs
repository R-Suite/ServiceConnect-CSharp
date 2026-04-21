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
    public void Publish_WithRoutingKey_SetsNamedDestinationAndDisplayName()
    {
        var args = new PublishEventArgs
        {
            RoutingKey = "orders",
            Message = new Message(Guid.NewGuid()),
            Headers = { ["MessageId"] = "msg-1" }
        };

        using var activity = ServiceConnectActivitySource.Publish(args);

        Assert.NotNull(activity);
        Assert.Equal("orders publish", activity!.DisplayName);
        Assert.Equal("orders", activity.GetTagItem(MessagingDestination));
        Assert.Equal("orders", activity.GetTagItem(MessagingDestinationRoutingKey));
        Assert.Equal("rabbitmq", activity.GetTagItem(MessagingSystem));
        Assert.Equal("publish", activity.GetTagItem(MessagingOperation));
        Assert.Equal("msg-1", activity.GetTagItem(MessageId));
    }

    [Fact]
    public void Publish_WithoutRoutingKey_MarksDestinationAnonymous()
    {
        var args = new PublishEventArgs
        {
            RoutingKey = "",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Publish(args);

        Assert.NotNull(activity);
        Assert.Equal("anonymous publish", activity!.DisplayName);
        Assert.Equal("true", activity.GetTagItem(MessagingDestinationAnonymous));
    }

    [Fact]
    public void Publish_EnricherThrows_RecordsEnrichmentException()
    {
        ServiceConnectActivitySource.Options.EnrichWithMessage =
            (_, _) => throw new InvalidOperationException("boom");

        var args = new PublishEventArgs
        {
            RoutingKey = "orders",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Publish(args);

        Assert.NotNull(activity);
        Assert.Equal("boom", activity!.GetTagItem("enrichment.exception"));
    }

    [Fact]
    public void Publish_WhenTelemetryDisabled_ReturnsNull()
    {
        ServiceConnectActivitySource.Options.EnablePublishTelemetry = false;

        var args = new PublishEventArgs
        {
            RoutingKey = "orders",
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
            RoutingKey = "orders",
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

    [Fact]
    public void Publish_WhenTelemetryDisabled_DoesNotTouchHeaders()
    {
        ServiceConnectActivitySource.Options.EnablePublishTelemetry = false;

        var args = new PublishEventArgs
        {
            RoutingKey = "orders",
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
        Assert.Equal("svc.queue publish", activity!.DisplayName);
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
        Assert.Equal("anonymous publish", activity!.DisplayName);
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
    public void Send_WithNullMessage_DoesNotInjectTraceparent()
    {
        // Send.Message can be null (caller-side path where telemetry is invoked with
        // only endpoint info). Ensure no trace header is injected in that branch.
        var args = new SendEventArgs
        {
            EndPoint = "svc.queue",
            Message = null
        };

        using var activity = ServiceConnectActivitySource.Send(args);

        Assert.NotNull(activity);
        Assert.False(args.Headers.ContainsKey("traceparent"));
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
}

[Collection("ActivityListener")]
public sealed class ServiceConnectActivitySource_NoListenerTests
{
    [Fact]
    public void Publish_ReturnsNull_WhenNoListeners()
    {
        var args = new PublishEventArgs
        {
            RoutingKey = "orders",
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
