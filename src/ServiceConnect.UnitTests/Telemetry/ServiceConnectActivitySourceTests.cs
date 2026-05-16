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
    private readonly ServiceConnectInstrumentationOptions _options = new();
    private readonly IMessagingSystemAttributes _attrs = new RabbitMqMessagingSystemAttributes();

    public ServiceConnectActivitySourceTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == ServiceConnectActivitySource.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        // Reset user-configurable enrichers in case a test set them.
        _options.EnrichWithMessage = null;
        _options.EnrichWithMessageBytes = null;
        _options.EnablePublishTelemetry = true;
        _options.EnableConsumeTelemetry = true;
        _options.EnableSendTelemetry = true;
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

        using var activity = ServiceConnectActivitySource.Publish(args, _options, _attrs);

        Assert.NotNull(activity);
        Assert.Equal("orders publish", activity!.DisplayName);
        Assert.Equal("orders", activity.GetTagItem(MessagingDestination));
        Assert.Equal("rabbitmq", activity.GetTagItem(MessagingSystem));
        Assert.Equal("publish", activity.GetTagItem(MessagingOperationType));
        Assert.Equal("publish", activity.GetTagItem(MessagingOperationName));
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

        using var activity = ServiceConnectActivitySource.Publish(args, _options, _attrs);

        Assert.NotNull(activity);
        Assert.Equal("OrderPlaced publish", activity!.DisplayName);
        Assert.Equal("OrderPlaced", activity.GetTagItem(MessagingDestination));
        Assert.Equal("high-priority", activity.GetTagItem(MessagingDestinationRoutingKey));
        // Explicit negative assertion: the routing key must not also appear on the
        // destination-name tag. The exchange assertion above usually catches a regression,
        // but a contrived case where exchange == routing-key would mask it.
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

        using var activity = ServiceConnectActivitySource.Publish(args, _options, _attrs);

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

        using var activity = ServiceConnectActivitySource.Publish(args, _options, _attrs);

        Assert.NotNull(activity);
        Assert.Equal("anonymous publish", activity!.DisplayName);
        Assert.Equal(true, activity.GetTagItem(MessagingDestinationAnonymous));
        Assert.Null(activity.GetTagItem(MessagingDestination));
    }

    [Fact]
    public void Publish_EnricherThrows_RecordsEnrichmentException()
    {
        var options = new ServiceConnectInstrumentationOptions
        {
            EnrichWithMessage = (_, _) => throw new InvalidOperationException("boom")
        };

        var args = new PublishEventArgs
        {
            Exchange = "orders",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Publish(args, options, _attrs);

        Assert.NotNull(activity);
        Assert.Equal("System.InvalidOperationException", activity!.GetTagItem("enrichment.exception"));
    }

    [Fact]
    public void Publish_WhenTelemetryDisabled_ReturnsNull()
    {
        var options = new ServiceConnectInstrumentationOptions { EnablePublishTelemetry = false };

        var args = new PublishEventArgs
        {
            Exchange = "orders",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Publish(args, options, _attrs);

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

        using var activity = ServiceConnectActivitySource.Publish(args, _options, _attrs);

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
        var options = new ServiceConnectInstrumentationOptions { EnablePublishTelemetry = false };

        var args = new PublishEventArgs
        {
            Exchange = "orders",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Publish(args, options, _attrs);

        Assert.Null(activity);
        Assert.False(args.Headers.ContainsKey("traceparent"));
    }

    // ---------------- Consume ----------------

    [Fact]
    public void Consume_SetsMessagingTags_AndDestinationFromHeader()
    {
        var args = new ConsumeEventArgs
        {
            Message = [1, 2, 3],
            Headers = new Dictionary<string, object>
            {
                ["DestinationAddress"] = Encoding.UTF8.GetBytes("svc.inbox"),
                ["MessageId"] = Encoding.UTF8.GetBytes("msg-42")
            }
        };

        using var activity = ServiceConnectActivitySource.Consume(args, _options, _attrs);

        Assert.NotNull(activity);
        Assert.Equal("svc.inbox process", activity!.DisplayName);
        Assert.Equal("svc.inbox", activity.GetTagItem(MessagingDestination));
        Assert.Equal("msg-42", activity.GetTagItem(MessageId));
        Assert.Equal("rabbitmq", activity.GetTagItem(MessagingSystem));
        Assert.Equal("process", activity.GetTagItem(MessagingOperationType));
        Assert.Equal("process", activity.GetTagItem(MessagingOperationName));
        Assert.Equal(3, activity.GetTagItem(MessagingBodySize));
    }

    [Fact]
    public void Consume_WithoutDestinationHeader_MarksAnonymous()
    {
        var args = new ConsumeEventArgs
        {
            Message = [],
            Headers = new Dictionary<string, object>()
        };

        using var activity = ServiceConnectActivitySource.Consume(args, _options, _attrs);

        Assert.NotNull(activity);
        Assert.Equal("anonymous process", activity!.DisplayName);
        Assert.Equal(true, activity.GetTagItem(MessagingDestinationAnonymous));
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

        using var activity = ServiceConnectActivitySource.Consume(args, _options, _attrs);

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

        using var activity = ServiceConnectActivitySource.Consume(args, _options, _attrs);

        Assert.NotNull(activity);
        Assert.Equal(traceId, activity!.TraceId.ToString());
        Assert.Equal(spanId, activity.ParentSpanId.ToString());
    }

    [Fact]
    public void Consume_WhenTelemetryDisabled_ReturnsNull()
    {
        var options = new ServiceConnectInstrumentationOptions { EnableConsumeTelemetry = false };

        var args = new ConsumeEventArgs
        {
            Headers = new Dictionary<string, object>()
        };

        using var activity = ServiceConnectActivitySource.Consume(args, options, _attrs);

        Assert.Null(activity);
    }

    [Fact]
    public void Consume_SetsMessagingMessageConversationId_FromCorrelationIdHeader()
    {
        var correlationId = Guid.NewGuid().ToString();
        var args = new ConsumeEventArgs
        {
            Headers = new Dictionary<string, object>
            {
                [HeaderKeys.CorrelationId] = Encoding.UTF8.GetBytes(correlationId),
                [HeaderKeys.DestinationAddress] = Encoding.UTF8.GetBytes("queue-a"),
            },
            Message = [1, 2, 3],
        };

        using var activity = ServiceConnectActivitySource.Consume(args, _options, _attrs);

        Assert.NotNull(activity);
        Assert.Equal(correlationId, activity!.GetTagItem(MessageConversationId));
    }

    // ---------------- server.address / server.port ----------------

    [Fact]
    public void Publish_WithServerAddress_StampsServerAddressAndPortTags()
    {
        // Attributes that supply a real broker address must surface server.address and
        // server.port so OTel backends can correlate spans across broker nodes.
        var attrsWithEndpoint = new RabbitMqMessagingSystemAttributes(
            new StubTransport { Host = "rabbit.internal", Port = 5672 });

        var args = new PublishEventArgs
        {
            Exchange = "orders",
            Message = new Message(Guid.NewGuid()),
        };

        using var activity = ServiceConnectActivitySource.Publish(args, _options, attrsWithEndpoint);

        Assert.NotNull(activity);
        Assert.Equal("rabbit.internal", activity!.GetTagItem(MessagingAttributes.ServerAddress));
        Assert.Equal(5672, activity.GetTagItem(MessagingAttributes.ServerPort));
    }

    [Fact]
    public void Consume_WithServerAddress_StampsServerAddressAndPortTags()
    {
        var attrsWithEndpoint = new RabbitMqMessagingSystemAttributes(
            new StubTransport { Host = "rabbit.internal", Port = 5672 });

        var args = new ConsumeEventArgs
        {
            Message = [1, 2, 3],
            Headers = new Dictionary<string, object>
            {
                [HeaderKeys.DestinationAddress] = System.Text.Encoding.UTF8.GetBytes("svc.inbox"),
            }
        };

        using var activity = ServiceConnectActivitySource.Consume(args, _options, attrsWithEndpoint);

        Assert.NotNull(activity);
        Assert.Equal("rabbit.internal", activity!.GetTagItem(MessagingAttributes.ServerAddress));
        Assert.Equal(5672, activity.GetTagItem(MessagingAttributes.ServerPort));
    }

    [Fact]
    public void Send_WithServerAddress_StampsServerAddressAndPortTags()
    {
        var attrsWithEndpoint = new RabbitMqMessagingSystemAttributes(
            new StubTransport { Host = "rabbit.internal", Port = 5672 });

        var args = new SendEventArgs
        {
            EndPoint = "svc.queue",
            Message = new Message(Guid.NewGuid()),
        };

        using var activity = ServiceConnectActivitySource.Send(args, _options, attrsWithEndpoint);

        Assert.NotNull(activity);
        Assert.Equal("rabbit.internal", activity!.GetTagItem(MessagingAttributes.ServerAddress));
        Assert.Equal(5672, activity.GetTagItem(MessagingAttributes.ServerPort));
    }

    [Fact]
    public void Publish_WithDefaultAttributes_OmitsServerAddressAndPortTags()
    {
        // When ServerAddress is empty and ServerPort is 0 (default impl values),
        // the tags must not be emitted rather than emitting empty-string / 0.
        var args = new PublishEventArgs
        {
            Exchange = "orders",
            Message = new Message(Guid.NewGuid()),
        };

        using var activity = ServiceConnectActivitySource.Publish(args, _options, _attrs);

        Assert.NotNull(activity);
        Assert.Null(activity!.GetTagItem(MessagingAttributes.ServerAddress));
        Assert.Null(activity.GetTagItem(MessagingAttributes.ServerPort));
    }

    [Fact]
    public void RabbitMqAttributes_MultiHostString_UsesFirstHost()
    {
        // Cluster host strings like "rabbit1,rabbit2" must emit only the first entry as
        // server.address, matching the single-value OTel semconv expectation.
        var attrs = new RabbitMqMessagingSystemAttributes(
            new StubTransport { Host = "rabbit1,rabbit2", Port = 5672 });

        Assert.Equal("rabbit1", attrs.ServerAddress);
    }

    [Fact]
    public void Consume_OperationType_IsProcess_NotReceive()
    {
        // Consume spans represent handler dispatch ("process"), not broker polling ("receive").
        var args = new ConsumeEventArgs
        {
            Message = [1],
            Headers = new Dictionary<string, object>
            {
                [HeaderKeys.DestinationAddress] = System.Text.Encoding.UTF8.GetBytes("queue-a"),
            }
        };

        using var activity = ServiceConnectActivitySource.Consume(args, _options, _attrs);

        Assert.NotNull(activity);
        Assert.Equal("process", activity!.GetTagItem(MessagingOperationType));
        Assert.Equal("process", activity.GetTagItem(MessagingOperationName));
    }

    // Stub transport used in server-address/port tests.
    private sealed class StubTransport : ServiceConnect.Interfaces.Configuration.ITransportConfiguration
    {
        private readonly Dictionary<string, object> _settings = [];

        public required string Host { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? VirtualHost { get; set; }
        public int RetryDelay { get; set; }
        public int MaxRetries { get; set; }
        public ushort PrefetchCount { get; set; }
        public int GracefulShutdownTimeoutMilliseconds { get; set; }
        public bool SslEnabled { get; set; }
        public bool SuppressPlaintextWarning { get; set; }
        public System.Net.Security.SslPolicyErrors AcceptablePolicyErrors { get; set; }
        public string? ServerName { get; set; }
        public string? CertPath { get; set; }
        public string? CertPassphrase { get; set; }
        public System.Security.Cryptography.X509Certificates.X509CertificateCollection? Certs { get; set; }
        public System.Security.Authentication.SslProtocols SslProtocol { get; set; }
        public System.Net.Security.LocalCertificateSelectionCallback? CertificateSelectionCallback { get; set; }
        public System.Net.Security.RemoteCertificateValidationCallback? CertificateValidationCallback { get; set; }
        public IReadOnlyDictionary<string, object> ClientSettings => _settings;
        public void SetClientSetting(string key, object value) => _settings[key] = value;

        // Convenience setter: routes the port into ClientSettings["Port"] where
        // RabbitMqMessagingSystemAttributes reads it.
        public int Port
        {
            get => _settings.TryGetValue("Port", out var v) ? Convert.ToInt32(v) : 0;
            set => _settings["Port"] = value;
        }
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

        using var activity = ServiceConnectActivitySource.Send(args, _options, _attrs);

        Assert.NotNull(activity);
        // The DisplayName carries "send" for per-destination tracing; the messaging.operation.type
        // tag is "publish" per OTel semconv (producer-side regardless of point-to-point vs pub/sub).
        Assert.Equal("svc.queue send", activity!.DisplayName);
        Assert.Equal("publish", activity.GetTagItem(MessagingOperationType));
        Assert.Equal("publish", activity.GetTagItem(MessagingOperationName));
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

        using var activity = ServiceConnectActivitySource.Send(args, _options, _attrs);

        Assert.NotNull(activity);
        Assert.Equal("anonymous send", activity!.DisplayName);
        Assert.Equal(true, activity.GetTagItem(MessagingDestinationAnonymous));
    }

    [Fact]
    public void Send_WhenTelemetryDisabled_ReturnsNull()
    {
        var options = new ServiceConnectInstrumentationOptions { EnableSendTelemetry = false };

        var args = new SendEventArgs
        {
            EndPoint = "svc.queue",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Send(args, options, _attrs);

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

        using var activity = ServiceConnectActivitySource.Send(args, _options, _attrs);

        Assert.NotNull(activity);
        Assert.True(args.Headers.TryGetValue("traceparent", out var traceparent));
        Assert.Matches("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$", traceparent);
        Assert.Contains(activity!.TraceId.ToString(), traceparent);
        Assert.Contains(activity.SpanId.ToString(), traceparent);
    }

    [Fact]
    public void Send_WithNullMessage_StillInjectsTraceparentHeader()
    {
        // Send must inject traceparent even when Message is null so that
        // payload-less sends still propagate W3C context across the broker.
        var args = new SendEventArgs
        {
            EndPoint = "svc.queue",
            Message = null
        };

        using var activity = ServiceConnectActivitySource.Send(args, _options, _attrs);

        // With an active listener the call starts an activity, making
        // Activity.Current non-null, so traceparent must be present.
        Assert.True(args.Headers.ContainsKey("traceparent"));
    }

    [Fact]
    public void Publish_WhenPublishTelemetryDisabled_StillInjectsTraceparentFromAmbient()
    {
        // An outer (e.g. ASP.NET) ambient activity must propagate across the broker
        // even when ServiceConnect's own Publish spans are disabled.
        var options = new ServiceConnectInstrumentationOptions { EnablePublishTelemetry = false };

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
        using var scActivity = ServiceConnectActivitySource.Publish(args, options, _attrs);

        Assert.Null(scActivity); // SC telemetry disabled → no SC span
        Assert.True(args.Headers.ContainsKey("traceparent")); // ambient context must be injected
    }

    [Fact]
    public void Send_WhenSendTelemetryDisabled_StillInjectsTraceparentFromAmbient()
    {
        // Send variant: symmetric to the Publish variant above.
        var options = new ServiceConnectInstrumentationOptions { EnableSendTelemetry = false };

        using var ambientListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "ambient-send",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(ambientListener);

        using var outerActivity = new ActivitySource("ambient-send").StartActivity("outer", ActivityKind.Server);
        Assert.NotNull(outerActivity);

        var args = new SendEventArgs { EndPoint = "svc.queue" };
        using var scActivity = ServiceConnectActivitySource.Send(args, options, _attrs);

        Assert.Null(scActivity); // SC telemetry disabled → no SC span
        Assert.True(args.Headers.ContainsKey("traceparent")); // ambient context must be injected
    }

    // ---------------- SetError ----------------

    [Fact]
    public void SetError_SetsActivityStatusToError_AndRecordsExceptionDetails()
    {
        // SetError must mark the activity as Error and attach exception metadata so
        // OTel backends surface it in error-rate dashboards.
        // AddException records details as an ActivityEvent named "exception", not as
        // activity-level tags, which is why we inspect Events rather than GetTagItem.
        var args = new PublishEventArgs { Exchange = "orders", Message = new Message(Guid.NewGuid()) };
        using var activity = ServiceConnectActivitySource.Publish(args, _options, _attrs);
        Assert.NotNull(activity);

        var ex = new InvalidOperationException("publish failed");
        ServiceConnectActivitySource.SetError(activity, ex, _options);

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
        var exception = Record.Exception(() => ServiceConnectActivitySource.SetError(null, ex, _options));
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
    public void Send_EmptyEndPoint_FallsBackToAnonymous()
    {
        // SendEventArgs carries one per-delivery endpoint. When that endpoint is empty
        // (e.g. publish-style sends with no resolved destination), the span tags as
        // anonymous rather than emitting an empty-string destination that would
        // pollute trace-by-destination dashboards.
        var args = new SendEventArgs
        {
            EndPoint = "",
            Headers = new Dictionary<string, string>(),
        };

        using var activity = ServiceConnectActivitySource.Send(args, _options, _attrs);

        Assert.NotNull(activity);
        Assert.Equal("anonymous send", activity!.DisplayName);
        Assert.Null(activity.GetTagItem(MessagingDestination));
        Assert.Equal(true, activity.GetTagItem(MessagingDestinationAnonymous));
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

        using var activity = ServiceConnectActivitySource.Send(args, _options, _attrs);

        Assert.NotNull(activity);
        Assert.Equal("publish", activity!.GetTagItem(MessagingOperationType));
        Assert.Equal("publish", activity.GetTagItem(MessagingOperationName));
        // Old attribute is GONE — OTel semconv update.
        Assert.Null(activity.GetTagItem("messaging.operation"));
    }

    [Fact]
    public void Publish_WithLinkedContext_SetsActivityParentNotLink()
    {
        var parentTraceId = ActivityTraceId.CreateRandom();
        var parentSpanId = ActivitySpanId.CreateRandom();
        var linkedContext = new ActivityContext(parentTraceId, parentSpanId, ActivityTraceFlags.Recorded);

        var args = new PublishEventArgs
        {
            Message = new Message(Guid.NewGuid()),
            Exchange = "exchange",
            Headers = new Dictionary<string, string>(),
        };

        using var activity = ServiceConnectActivitySource.Publish(args, _options, _attrs, linkedContext);

        Assert.NotNull(activity);
        Assert.Equal(parentTraceId, activity!.TraceId);
        Assert.Equal(parentSpanId, activity.ParentSpanId);
    }

    [Fact]
    public void Send_WithLinkedContext_SetsActivityParentNotLink()
    {
        var parentTraceId = ActivityTraceId.CreateRandom();
        var parentSpanId = ActivitySpanId.CreateRandom();
        var linkedContext = new ActivityContext(parentTraceId, parentSpanId, ActivityTraceFlags.Recorded);

        var args = new SendEventArgs
        {
            Message = new Message(Guid.NewGuid()),
            EndPoint = "queue-a",
            Headers = new Dictionary<string, string>(),
        };

        using var activity = ServiceConnectActivitySource.Send(args, _options, _attrs, linkedContext);

        Assert.NotNull(activity);
        Assert.Equal(parentTraceId, activity!.TraceId);
        Assert.Equal(parentSpanId, activity.ParentSpanId);
    }

    [Fact]
    public void Consume_WithTraceParent_KeepsParentSemantics()
    {
        // Consume legitimately wants parent-context: the W3C traceparent header is
        // the actual upstream span, and the consume span IS its child.
        var producerTraceId = ActivityTraceId.CreateRandom();
        var producerSpanId = ActivitySpanId.CreateRandom();
        var traceParent = $"00-{producerTraceId}-{producerSpanId}-01";

        var args = new ConsumeEventArgs
        {
            Headers = new Dictionary<string, object>
            {
                ["traceparent"] = traceParent,
                [HeaderKeys.DestinationAddress] = "queue-a",
            },
            Message = [1],
        };

        using var activity = ServiceConnectActivitySource.Consume(args, _options, _attrs);

        Assert.NotNull(activity);
        // Consume IS a child of the producer — same trace.
        Assert.Equal(producerTraceId, activity!.TraceId);
        // Not exposed as a link; it's the actual parent.
        Assert.Empty(activity.Links);
    }

    [Fact]
    public void Publish_InjectsTraceContextExactlyOnce_WhenListenerAttached()
    {
        var injectCount = 0;
        var originalPropagator = DistributedContextPropagator.Current;
        try
        {
            DistributedContextPropagator.Current = new CountingPropagator(() => injectCount++);

            var args = new PublishEventArgs
            {
                Message = new Message(Guid.NewGuid()),
                Exchange = "exchange",
                Headers = new Dictionary<string, string>(),
            };

            using var activity = ServiceConnectActivitySource.Publish(args, _options, _attrs);
            Assert.NotNull(activity);

            Assert.Equal(1, injectCount);
        }
        finally
        {
            DistributedContextPropagator.Current = originalPropagator;
        }
    }

    [Fact]
    public void InjectHeader_UnsupportedCarrier_WritesWarningOnce()
    {
        var listener = new RecordingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            ServiceConnectActivitySource.ResetCarrierWarnedFlagForTest();

            // Two invocations of InjectHeaderForTest with the wrong carrier shape:
            ServiceConnectActivitySource.InvokeInjectHeaderForTest(new Dictionary<string, object>(), "k", "v");
            ServiceConnectActivitySource.InvokeInjectHeaderForTest(new Dictionary<string, object>(), "k", "v");

            Assert.Single(listener.Warnings, w => w.Contains("InjectHeader"));
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public void InjectHeader_StringDictionaryCarrier_WritesNoWarning()
    {
        var listener = new RecordingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            ServiceConnectActivitySource.ResetCarrierWarnedFlagForTest();

            // Correct carrier shape — should NOT trigger any warning.
            var carrier = new Dictionary<string, string>();
            ServiceConnectActivitySource.InvokeInjectHeaderForTest(carrier, "k", "v");
            ServiceConnectActivitySource.InvokeInjectHeaderForTest(carrier, "k2", "v2");

            Assert.DoesNotContain(listener.Warnings, w => w.Contains("InjectHeader"));
            // Headers were correctly written:
            Assert.Equal("v", carrier["k"]);
            Assert.Equal("v2", carrier["k2"]);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    private sealed class RecordingTraceListener : TraceListener
    {
        public List<string> Warnings { get; } = [];
        public override void TraceEvent(TraceEventCache? cache, string source, TraceEventType type, int id, string? message)
        {
            if (type == TraceEventType.Warning && message is not null)
            {
                Warnings.Add(message);
            }
        }
        public override void TraceEvent(TraceEventCache? cache, string source, TraceEventType type, int id, string? format, params object?[]? args)
        {
            if (type == TraceEventType.Warning && format is not null)
            {
                Warnings.Add(args is { Length: > 0 } ? string.Format(format, args) : format);
            }
        }
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }
    }

    private sealed class CountingPropagator(Action onInject) : DistributedContextPropagator
    {
        public override IReadOnlyCollection<string> Fields => [];
        public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter)
            => onInject();
        public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState)
        { traceId = null; traceState = null; }
        public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter)
            => null;
    }

    [Fact]
    public void Consume_MalformedTraceparent_FallsBackToActivityCurrent()
    {
        using var ambient = new Activity("ambient").Start();

        var args = new ConsumeEventArgs
        {
            Message = [1],
            Type = "FakeMessage",
            Headers = new Dictionary<string, object>
            {
                ["traceparent"] = "this-is-not-a-valid-traceparent",
            },
        };

        using var activity = ServiceConnectActivitySource.Consume(args, _options, _attrs);

        Assert.NotNull(activity);
        Assert.Equal(ambient.TraceId, activity!.TraceId);
    }

    [Fact]
    public void Publish_EnricherThrowsOce_DisposesActivity_AndRestoresAmbientCurrent()
    {
        var ambient = new Activity("ambient").Start();

        var options = new ServiceConnectInstrumentationOptions
        {
            EnrichWithMessage = (_, _) => throw new OperationCanceledException("co-op cancel"),
        };

        var args = new PublishEventArgs
        {
            Message = new Message(Guid.NewGuid()),
            Exchange = "exchange",
            Headers = new Dictionary<string, string>(),
        };

        var thrown = Assert.Throws<OperationCanceledException>(() =>
            ServiceConnectActivitySource.Publish(args, options, _attrs));

        Assert.Equal("co-op cancel", thrown.Message);
        // The activity that Publish started must be disposed before the OCE escapes,
        // so Activity.Current is the outer ambient activity, not a leaked publish span.
        Assert.Same(ambient, Activity.Current);

        ambient.Dispose();
    }

    [Fact]
    public void Send_EnricherThrowsOce_DisposesActivity_AndRestoresAmbientCurrent()
    {
        var ambient = new Activity("ambient").Start();

        var options = new ServiceConnectInstrumentationOptions
        {
            EnrichWithMessage = (_, _) => throw new OperationCanceledException("co-op cancel"),
        };

        var args = new SendEventArgs
        {
            Message = new Message(Guid.NewGuid()),
            EndPoint = "queue-a",
            Headers = new Dictionary<string, string>(),
        };

        Assert.Throws<OperationCanceledException>(() =>
            ServiceConnectActivitySource.Send(args, options, _attrs));

        Assert.Same(ambient, Activity.Current);

        ambient.Dispose();
    }

    [Fact]
    public void Consume_EnricherThrowsOce_DisposesActivity_AndRestoresAmbientCurrent()
    {
        var ambient = new Activity("ambient").Start();

        var options = new ServiceConnectInstrumentationOptions
        {
            EnrichWithMessageBytes = (_, _) => throw new OperationCanceledException("co-op cancel"),
        };

        var args = new ConsumeEventArgs
        {
            Message = [1, 2, 3],
            Type = "FakeMessage",
            Headers = new Dictionary<string, object>(),
        };

        Assert.Throws<OperationCanceledException>(() =>
            ServiceConnectActivitySource.Consume(args, options, _attrs));

        Assert.Same(ambient, Activity.Current);

        ambient.Dispose();
    }

    // ---------------- MaxTagValueLength truncation ----------------

    [Fact]
    public void Publish_HeaderValueExceedsMaxTagValueLength_TruncatesTag()
    {
        var longRoutingKey = new string('a', 500);
        var options = new ServiceConnectInstrumentationOptions { MaxTagValueLength = 100 };

        var args = new PublishEventArgs
        {
            Message = new Message(Guid.NewGuid()),
            Exchange = "exchange",
            RoutingKey = longRoutingKey,
            Headers = new Dictionary<string, string>(),
        };

        using var activity = ServiceConnectActivitySource.Publish(args, options, _attrs);
        Assert.NotNull(activity);

        var routingKeyTag = activity.GetTagItem(MessagingAttributes.MessagingDestinationRoutingKey)?.ToString();
        Assert.NotNull(routingKeyTag);
        Assert.Equal(100, routingKeyTag.Length);
        Assert.Equal(new string('a', 100), routingKeyTag);
    }

    [Fact]
    public void Publish_HeaderValueWithinMaxTagValueLength_TagsVerbatim()
    {
        var shortRoutingKey = "short.routing.key";
        var options = new ServiceConnectInstrumentationOptions { MaxTagValueLength = 100 };

        var args = new PublishEventArgs
        {
            Message = new Message(Guid.NewGuid()),
            Exchange = "exchange",
            RoutingKey = shortRoutingKey,
            Headers = new Dictionary<string, string>(),
        };

        using var activity = ServiceConnectActivitySource.Publish(args, options, _attrs);
        Assert.NotNull(activity);

        var routingKeyTag = activity.GetTagItem(MessagingAttributes.MessagingDestinationRoutingKey)?.ToString();
        Assert.Equal(shortRoutingKey, routingKeyTag);
    }

    // ---------------- ExceptionMessageSanitiser ----------------

    [Fact]
    public void SetError_WithSanitiser_AppliesToStatusAndExceptionEventTag()
    {
        var options = new ServiceConnectInstrumentationOptions
        {
            ExceptionMessageSanitiser = ex => "REDACTED",
        };

        ActivityStatusCode observedStatus = ActivityStatusCode.Unset;
        string? observedDescription = null;
        Dictionary<string, object?>? observedExceptionTags = null;

        var capturingListener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == ServiceConnectActivitySource.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                observedStatus = a.Status;
                observedDescription = a.StatusDescription;
                var ev = a.Events.FirstOrDefault(e => e.Name == "exception");
                if (ev.Tags is not null)
                {
                    observedExceptionTags = ev.Tags.ToDictionary(t => t.Key, t => t.Value);
                }
            },
        };
        ActivitySource.AddActivityListener(capturingListener);
        try
        {
            using var activity = new ActivitySource(ServiceConnectActivitySource.ActivitySourceName).StartActivity("test");
            Assert.NotNull(activity);

            ServiceConnectActivitySource.SetError(activity, new InvalidOperationException("sensitive: connection-string=secret"), options);

            activity.Dispose();

            Assert.Equal(ActivityStatusCode.Error, observedStatus);
            Assert.Equal("REDACTED", observedDescription);
            Assert.NotNull(observedExceptionTags);
            Assert.Equal("REDACTED", observedExceptionTags!["exception.message"]);
        }
        finally
        {
            capturingListener.Dispose();
        }
    }

    // ---------------- IsAllDataRequested guards ----------------

    // NOTE: This test cannot use the fixture's pre-registered AllData listener because AllData
    // beats PropagationData and IsAllDataRequested would always be true. The test is placed here
    // for organisational proximity but uses its own isolated listener pattern: see
    // ServiceConnectActivitySource_PropagationOnlyTests below for the actual guard coverage.

    // ---------------- Empty-Guid CorrelationId ----------------

    [Fact]
    public void Publish_EmptyCorrelationId_DoesNotSetConversationIdTag()
    {
        var args = new PublishEventArgs
        {
            Message = new Message(Guid.Empty),
            Exchange = "exchange",
            Headers = new Dictionary<string, string>(),
        };

        using var activity = ServiceConnectActivitySource.Publish(args, _options, _attrs);
        Assert.NotNull(activity);
        Assert.Null(activity.GetTagItem(MessagingAttributes.MessageConversationId));
    }

    [Fact]
    public void Publish_NonEmptyCorrelationId_SetsConversationIdTag()
    {
        var cid = Guid.NewGuid();
        var args = new PublishEventArgs
        {
            Message = new Message(cid),
            Exchange = "exchange",
            Headers = new Dictionary<string, string>(),
        };

        using var activity = ServiceConnectActivitySource.Publish(args, _options, _attrs);
        Assert.NotNull(activity);
        Assert.Equal(cid.ToString(), activity.GetTagItem(MessagingAttributes.MessageConversationId)?.ToString());
    }

    // ---------------- Single ActivitySource verification ----------------

    [Fact]
    public void Publish_Send_Consume_AllEmitOnSingleActivitySource()
    {
        var sourceNames = new HashSet<string>();
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => sourceNames.Add(a.Source.Name),
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            using (ServiceConnectActivitySource.Publish(
                new PublishEventArgs { Message = new Message(Guid.NewGuid()), Exchange = "x", Headers = new Dictionary<string, string>() },
                _options, _attrs)) { }
            using (ServiceConnectActivitySource.Send(
                new SendEventArgs { Message = new Message(Guid.NewGuid()), EndPoint = "q", Headers = new Dictionary<string, string>() },
                _options, _attrs)) { }
            using (ServiceConnectActivitySource.Consume(
                new ConsumeEventArgs { Message = [1], Type = "T", Headers = new Dictionary<string, object>() },
                _options, _attrs)) { }

            Assert.Single(sourceNames);
            Assert.Contains(ServiceConnectActivitySource.ActivitySourceName, sourceNames);
        }
        finally
        {
            listener.Dispose();
        }
    }

    // ---------------- Sanitiser-bypass via exception.stacktrace ----------------

    [Fact]
    public void SetError_WithSanitiser_StacktraceTagDoesNotContainRawMessage()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        using var source = new ActivitySource("ServiceConnectActivitySourceTests-Sanitiser");
        using var activity = source.StartActivity("op");
        Assert.NotNull(activity);

        Exception thrown;
        try
        {
            throw new InvalidOperationException("RAW-SECRET-MESSAGE");
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        var options = new ServiceConnectInstrumentationOptions
        {
            ExceptionMessageSanitiser = _ => "REDACTED",
        };

        ServiceConnectActivitySource.SetError(activity, thrown, options);

        var exceptionEvent = activity!.Events.Single(e => e.Name == "exception");
        var stacktrace = exceptionEvent.Tags.Single(t => t.Key == "exception.stacktrace").Value?.ToString() ?? string.Empty;
        var message = exceptionEvent.Tags.Single(t => t.Key == "exception.message").Value?.ToString() ?? string.Empty;

        Assert.DoesNotContain("RAW-SECRET-MESSAGE", stacktrace);
        Assert.Equal("REDACTED", message);
    }
}

[Collection("ActivityListener")]
public sealed class ServiceConnectActivitySource_NoListenerTests
{
    private readonly ServiceConnectInstrumentationOptions _options = new();
    private readonly IMessagingSystemAttributes _attrs = new RabbitMqMessagingSystemAttributes();

    [Fact]
    public void Publish_ReturnsNull_WhenNoListeners()
    {
        var args = new PublishEventArgs
        {
            Exchange = "orders",
            Message = new Message(Guid.NewGuid())
        };

        using var activity = ServiceConnectActivitySource.Publish(args, _options, _attrs);

        Assert.Null(activity);
    }

    [Fact]
    public void Consume_ReturnsNull_WhenNoListeners()
    {
        var args = new ConsumeEventArgs();

        using var activity = ServiceConnectActivitySource.Consume(args, _options, _attrs);

        Assert.Null(activity);
    }

    [Fact]
    public void Send_ReturnsNull_WhenNoListeners()
    {
        var args = new SendEventArgs { EndPoint = "ep" };

        using var activity = ServiceConnectActivitySource.Send(args, _options, _attrs);

        Assert.Null(activity);
    }

    [Fact]
    public void IsConsumeTelemetryEnabled_NoListener_ReturnsFalse()
    {
        var options = new ServiceConnectInstrumentationOptions { EnableConsumeTelemetry = true };
        Assert.False(ServiceConnectActivitySource.IsConsumeTelemetryEnabled(options));
    }
}

[Collection("ActivityListener")]
public sealed class ServiceConnectActivitySource_PropagationOnlyTests
{
    private readonly ServiceConnectInstrumentationOptions _options = new();
    private readonly IMessagingSystemAttributes _attrs = new RabbitMqMessagingSystemAttributes();

    // Tests in this fixture register only a PropagationData listener so that
    // IsAllDataRequested is false. A co-existing AllData listener would win and
    // make IsAllDataRequested always true, defeating the guard coverage.

    [Fact]
    public void Publish_SampleDroppedActivity_DoesNotSetUserTags()
    {
        var droppingListener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == ServiceConnectActivitySource.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.PropagationData,
        };
        ActivitySource.AddActivityListener(droppingListener);
        try
        {
            var args = new PublishEventArgs
            {
                Message = new Message(Guid.NewGuid()),
                Exchange = "exchange",
                RoutingKey = "rk",
                Headers = new Dictionary<string, string>(),
            };

            using var activity = ServiceConnectActivitySource.Publish(args, _options, _attrs);
            Assert.NotNull(activity);
            Assert.False(activity.IsAllDataRequested);

            Assert.Null(activity.GetTagItem(MessagingAttributes.MessagingDestination));
            Assert.Null(activity.GetTagItem(MessagingAttributes.MessagingDestinationRoutingKey));
        }
        finally
        {
            droppingListener.Dispose();
        }
    }

    // ---------------- TelemetrySendMiddleware: anonymous destination on Publish ----------------

    [Fact]
    public async Task Publish_ViaSendMiddleware_StampsAnonymousDestination()
    {
        // The send middleware leaves Exchange empty for anonymous publishes so the span
        // surfaces as anonymous; the routing-key tag is still stamped to preserve
        // RabbitMQ-specific routing observability.
        Activity? captured = null;
        using var capture = new ActivityListener
        {
            ShouldListenTo = src => src.Name == ServiceConnectActivitySource.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => captured = a,
        };
        ActivitySource.AddActivityListener(capture);
        try
        {
            var middleware = new TelemetrySendMiddleware(_options, _attrs);
            var ctx = new SendContext
            {
                Message = new MiddlewareTestMessage(Guid.NewGuid()),
                MessageType = typeof(MiddlewareTestMessage),
                MessageBytes = ReadOnlyMemory<byte>.Empty,
                Headers = new Dictionary<string, string>(),
                EndPoint = null,
                RoutingKey = "high-priority",
                Operation = SendOperation.Publish,
            };

            await middleware.ProcessAsync(ctx, (_, _) => Task.CompletedTask, CancellationToken.None);

            Assert.NotNull(captured);
            Assert.Null(captured!.GetTagItem(MessagingDestination));  // no destination — anonymous.
            Assert.Equal(true, captured.GetTagItem(MessagingDestinationAnonymous));
            // Routing key is still preserved for RabbitMQ-specific routing observability.
            Assert.Equal("high-priority", captured.GetTagItem(MessagingDestinationRoutingKey));
            // New OTel pair.
            Assert.Equal("publish", captured.GetTagItem(MessagingOperationType));
            Assert.Equal("publish", captured.GetTagItem(MessagingOperationName));
            // Old attribute is GONE.
            Assert.Null(captured.GetTagItem("messaging.operation"));
        }
        finally
        {
            capture.Dispose();
        }
    }

    private sealed class MiddlewareTestMessage(Guid correlationId) : Message(correlationId);
}
