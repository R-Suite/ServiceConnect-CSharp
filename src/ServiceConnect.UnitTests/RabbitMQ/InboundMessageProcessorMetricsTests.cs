using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.UnitTests.Diagnostics;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Asserts that the two ack-and-drop catch-blocks in <see cref="InboundMessageProcessor"/> emit
/// the correct ServiceConnect-extension counters with the spec'd tag subsets:
/// <list type="bullet">
///   <item><c>messaging.serviceconnect.retry.drops</c> — carries <c>messaging.destination.name</c>
///         (the original queue) and <c>error.type</c>.</item>
///   <item><c>messaging.serviceconnect.audit.drops</c> — NO <c>messaging.destination.name</c>
///         tag (audit queue is global), only <c>messaging.system</c> + <c>error.type</c>.</item>
/// </list>
/// </summary>
public sealed class InboundMessageProcessorMetricsTests
{
    private static BasicDeliverEventArgs MakeArgs() => new(
        consumerTag: "ct",
        deliveryTag: 1,
        redelivered: false,
        exchange: "",
        routingKey: "q",
        properties: new BasicProperties(),
        body: new byte[] { 1, 2, 3 });

    [Fact]
    public async Task ProcessAsync_OnRetryPublishFailure_IncrementsRetryDrops()
    {
        // Per-test unique queue name so the MetricCollector tag-filter isolates the emission
        // from any other tests running in parallel that hit the same instrument.
        var queueName = $"q-retrydrop-{Guid.NewGuid():N}";
        using var collector = new MetricCollector("messaging.destination.name", queueName);

        // Channel throws a non-transport, non-OCE exception on publish — that's the swallow-and-ack
        // branch that emits RetryDrop.
        var channelMock = new Mock<IChannel>();
        var poisonException = new InvalidOperationException("retry queue gone");
        channelMock
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(poisonException);

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns(queueName);
        queueConfig.SetupGet(q => q.AuditingEnabled).Returns(false);
        queueConfig.SetupGet(q => q.AuditQueueName).Returns("audit");
        queueConfig.SetupGet(q => q.AuditRoutingKey).Returns(string.Empty);

        var auditPublisher = new MessageAuditPublisher(queueConfig.Object);
        var retryHandler = new MessageRetryHandler(maxRetries: 3, errorExchange: "err", NullLogger.Instance);

        // Handler returns Success=false so the retry-publish branch fires.
        static Task<ConsumeEventResult> HandleAsync(ReadOnlyMemory<byte> _, string __, IDictionary<string, object> ___, CancellationToken ____) =>
            Task.FromResult(new ConsumeEventResult { Success = false });

        var processor = new InboundMessageProcessor(
            consumerEventHandler: (ConsumerEventHandler)HandleAsync,
            retryHandler: retryHandler,
            auditPublisher: auditPublisher,
            queueConfiguration: queueConfig.Object,
            timeProvider: TimeProvider.System,
            logger: NullLogger.Instance,
            retryQueueName: queueName + ".Retries",
            errorsDisabled: false,
            deadLetterUnhandledMessages: false,
            includeMachineNameInHeaders: false,
            shutdownTimedOut: () => false,
            shutdownPublishToken: () => CancellationToken.None);

        // The catch swallows the poison exception and returns true → message is acked.
        var processed = await processor.ProcessAsync(channelMock.Object, MakeArgs(), CancellationToken.None);
        Assert.True(processed);

        var record = Assert.Single(collector.GetLongRecords(MetricNames.RetryDrops));
        Assert.Equal(1, record.Value);
        Assert.Equal("rabbitmq", record.GetTag("messaging.system"));
        Assert.Equal(queueName, record.GetTag("messaging.destination.name"));
        // ExceptionTypeMapper falls through to GetType().Name for non-allow-listed types.
        Assert.Equal(nameof(InvalidOperationException), record.GetTag("error.type"));
    }

    [Fact]
    public async Task ProcessAsync_OnAuditPublishFailure_IncrementsAuditDrops()
    {
        // Audit drop has NO messaging.destination.name tag (audit queue is global) so
        // MetricCollector cannot filter on that key. Instead we use a per-test custom
        // exception type whose runtime GetType().Name is unique to this test, then
        // filter the collector on error.type to isolate from any other test in flight.
        var expectedErrorType = nameof(AuditDropProbeException);
        using var collector = new MetricCollector("error.type", expectedErrorType);

        var channelMock = new Mock<IChannel>();
        channelMock
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuditDropProbeException());

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("q-auditdrop");
        queueConfig.SetupGet(q => q.AuditingEnabled).Returns(true);
        queueConfig.SetupGet(q => q.AuditQueueName).Returns("audit");
        queueConfig.SetupGet(q => q.AuditRoutingKey).Returns(string.Empty);

        var auditPublisher = new MessageAuditPublisher(queueConfig.Object);
        var retryHandler = new MessageRetryHandler(maxRetries: 0, errorExchange: "err", NullLogger.Instance);

        // Handler returns Success=true, NotHandled=false → audit branch fires.
        static Task<ConsumeEventResult> HandleAsync(ReadOnlyMemory<byte> _, string __, IDictionary<string, object> ___, CancellationToken ____) =>
            Task.FromResult(new ConsumeEventResult { Success = true, NotHandled = false });

        var processor = new InboundMessageProcessor(
            consumerEventHandler: (ConsumerEventHandler)HandleAsync,
            retryHandler: retryHandler,
            auditPublisher: auditPublisher,
            queueConfiguration: queueConfig.Object,
            timeProvider: TimeProvider.System,
            logger: NullLogger.Instance,
            retryQueueName: "q-auditdrop.Retries",
            errorsDisabled: false,
            deadLetterUnhandledMessages: false,
            includeMachineNameInHeaders: false,
            shutdownTimedOut: () => false,
            shutdownPublishToken: () => CancellationToken.None);

        var processed = await processor.ProcessAsync(channelMock.Object, MakeArgs(), CancellationToken.None);
        Assert.True(processed);

        var record = Assert.Single(collector.GetLongRecords(MetricNames.AuditDrops));
        Assert.Equal(1, record.Value);
        Assert.Equal("rabbitmq", record.GetTag("messaging.system"));
        Assert.Equal(expectedErrorType, record.GetTag("error.type"));
        // Spec invariant: audit drop carries no destination-name tag (audit queue is global).
        Assert.Null(record.GetTag("messaging.destination.name"));
    }

    // Custom exception type whose runtime GetType().Name is unique to this test file —
    // any other test that emits an audit drop with a different exception type will not
    // match the MetricCollector's error.type filter.
#pragma warning disable MA0048 // multiple types in one file — tightly-scoped test probe
    private sealed class AuditDropProbeException : Exception
    {
        public AuditDropProbeException() : base("audit publish failed in metrics test") { }
    }
#pragma warning restore MA0048
}
