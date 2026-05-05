using Microsoft.Extensions.Logging;
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
/// Drives <see cref="RabbitMqDispatchPipeline"/> directly, asserting:
/// <list type="bullet">
///   <item>Handler outcome → ack/nack frame mapping (success → ack, retry → nack-with-requeue, throw → nack-with-requeue).</item>
///   <item>messaging.process.* metric tags (outcome=success/error/retry, error.type for error).</item>
///   <item>Channel-state guards (null channel, closed channel, shutdown-timed-out) suppress ack/nack and log at Debug.</item>
///   <item>AlreadyClosed / ObjectDisposed during shutdown demote to Debug; outside shutdown route via LogAckOrNackFailure.</item>
///   <item>The host-side rejection paths (validator-rejected → AckOrNackAsync(processed=true);
///         null-processor → AckOrNackAsync(processed=false)) drive the right frame.</item>
/// </list>
/// Mirrors <c>ConsumerProcessMetricsTests</c> for the metric assertions and the test harness pattern.
/// </summary>
public sealed class RabbitMqDispatchPipelineTests
{
    [Fact]
    public async Task DispatchAndAckAsync_HandlerSuccess_AcksAndEmitsSuccessOutcome()
    {
        var queueName = $"q-pipe-success-{Guid.NewGuid():N}";
        using var collector = new MetricCollector("messaging.destination.name", queueName);

        var consumerChannel = BuildConsumerChannel(isOpen: true);
        var publishChannel = BuildPublishChannel();
        var processor = BuildProcessor(queueName, (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }));
        var pipeline = new RabbitMqDispatchPipeline(
            queueName,
            shutdownTimedOutQuery: () => false,
            shutdownStartedQuery: () => false,
            NullLogger.Instance);

        var args = MakeArgs(deliveryTag: 11);
        await pipeline.DispatchAndAckAsync(processor, consumerChannel.Object, publishChannel.Object, args, CancellationToken.None);

        consumerChannel.Verify(c => c.BasicAckAsync(11UL, false, It.IsAny<CancellationToken>()), Times.Once);
        consumerChannel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);

        var duration = Assert.Single(collector.GetDoubleRecords(MetricNames.ProcessDuration));
        Assert.Equal(queueName, duration.GetTag("messaging.destination.name"));
        Assert.Null(duration.GetTag("error.type"));

        var consumed = Assert.Single(collector.GetLongRecords(MetricNames.ConsumedMessages));
        Assert.Equal("success", consumed.GetTag("messaging.outcome"));
        Assert.Null(consumed.GetTag("error.type"));
    }

    [Fact]
    public async Task DispatchAndAckAsync_HandlerRetry_NacksWithRequeueAndEmitsRetryOutcome()
    {
        // Handler returns Success=false but does NOT throw. The pipeline's processor is
        // configured so the publish-retry path succeeds — that lets us hit the case where
        // ProcessAsync returns false (processed=false) without an exception, which maps to
        // outcome=retry. The dispatch then nacks-with-requeue against the model channel.
        var queueName = $"q-pipe-retry-{Guid.NewGuid():N}";
        using var collector = new MetricCollector("messaging.destination.name", queueName);

        var consumerChannel = BuildConsumerChannel(isOpen: true);
        var publishChannel = BuildPublishChannel();
        // shutdownTimedOut=true so the InboundMessageProcessor's shutdown-aware retry path
        // returns false without publishing or throwing — that's the documented retry outcome.
        var processor = BuildProcessor(queueName,
            (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = false, Exception = new InvalidOperationException("h") }),
            shutdownTimedOut: true);
        var pipeline = new RabbitMqDispatchPipeline(
            queueName,
            shutdownTimedOutQuery: () => false,  // false here so dispatch's ack/nack happens
            shutdownStartedQuery: () => false,
            NullLogger.Instance);

        var args = MakeArgs(deliveryTag: 12);
        await pipeline.DispatchAndAckAsync(processor, consumerChannel.Object, publishChannel.Object, args, CancellationToken.None);

        consumerChannel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        consumerChannel.Verify(c => c.BasicNackAsync(12UL, false, true, It.IsAny<CancellationToken>()), Times.Once);

        var consumed = Assert.Single(collector.GetLongRecords(MetricNames.ConsumedMessages));
        Assert.Equal("retry", consumed.GetTag("messaging.outcome"));
        Assert.Null(consumed.GetTag("error.type"));
    }

    [Fact]
    public async Task DispatchAndAckAsync_HandlerThrows_NacksWithRequeueAndEmitsErrorOutcome()
    {
        var queueName = $"q-pipe-error-{Guid.NewGuid():N}";
        using var collector = new MetricCollector("messaging.destination.name", queueName);

        var consumerChannel = BuildConsumerChannel(isOpen: true);
        var publishChannel = BuildPublishChannel(retryPublishThrows: true);
        // Handler returns Success=false; with retry-publish throwing AlreadyClosedException
        // (on InboundMessageProcessor's rethrow list), ProcessAsync re-raises and the pipeline
        // catches → outcome=error. processed stays false → dispatch nacks-with-requeue.
        var processor = BuildProcessor(queueName,
            (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = false, Exception = new InvalidOperationException("h") }));
        var pipeline = new RabbitMqDispatchPipeline(
            queueName,
            shutdownTimedOutQuery: () => false,
            shutdownStartedQuery: () => false,
            NullLogger.Instance);

        var args = MakeArgs(deliveryTag: 13);
        await pipeline.DispatchAndAckAsync(processor, consumerChannel.Object, publishChannel.Object, args, CancellationToken.None);

        consumerChannel.Verify(c => c.BasicNackAsync(13UL, false, true, It.IsAny<CancellationToken>()), Times.Once);

        var duration = Assert.Single(collector.GetDoubleRecords(MetricNames.ProcessDuration));
        Assert.NotNull(duration.GetTag("error.type"));
        var consumed = Assert.Single(collector.GetLongRecords(MetricNames.ConsumedMessages));
        Assert.Equal("error", consumed.GetTag("messaging.outcome"));
        Assert.NotNull(consumed.GetTag("error.type"));
    }

    [Fact]
    public async Task AckOrNackAsync_DirectAck_AcksWithoutEmittingMetrics()
    {
        // Mirrors the validator-rejection path: we already know the outcome (ack-and-don't-redeliver)
        // and there's no handler dispatch, so no messaging.process.* emission should happen.
        var queueName = $"q-pipe-direct-ack-{Guid.NewGuid():N}";
        using var collector = new MetricCollector("messaging.destination.name", queueName);

        var consumerChannel = BuildConsumerChannel(isOpen: true);
        var pipeline = new RabbitMqDispatchPipeline(
            queueName, () => false, () => false, NullLogger.Instance);

        var args = MakeArgs(deliveryTag: 21);
        await pipeline.AckOrNackAsync(consumerChannel.Object, args, processed: true);

        consumerChannel.Verify(c => c.BasicAckAsync(21UL, false, It.IsAny<CancellationToken>()), Times.Once);
        consumerChannel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(collector.GetDoubleRecords(MetricNames.ProcessDuration));
        Assert.Empty(collector.GetLongRecords(MetricNames.ConsumedMessages));
    }

    [Fact]
    public async Task AckOrNackAsync_DirectNack_NacksWithRequeueWithoutEmittingMetrics()
    {
        // Mirrors the null-processor path: the host knows the message wasn't handled and
        // should be requeued for the next consumer start. No metric emission.
        var queueName = $"q-pipe-direct-nack-{Guid.NewGuid():N}";
        using var collector = new MetricCollector("messaging.destination.name", queueName);

        var consumerChannel = BuildConsumerChannel(isOpen: true);
        var pipeline = new RabbitMqDispatchPipeline(
            queueName, () => false, () => false, NullLogger.Instance);

        var args = MakeArgs(deliveryTag: 22);
        await pipeline.AckOrNackAsync(consumerChannel.Object, args, processed: false);

        consumerChannel.Verify(c => c.BasicNackAsync(22UL, false, true, It.IsAny<CancellationToken>()), Times.Once);
        consumerChannel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(collector.GetLongRecords(MetricNames.ConsumedMessages));
    }

    [Fact]
    public async Task AckOrNackAsync_ChannelNull_LogsDebug_NoFrameSent()
    {
        var capturedLogs = new List<CapturedLog>();
        var pipeline = new RabbitMqDispatchPipeline(
            "q", () => false, () => false, BuildCapturingLogger(capturedLogs));

        await pipeline.AckOrNackAsync(model: null, MakeArgs(deliveryTag: 31), processed: true);

        Assert.Contains(capturedLogs, l => l.Level == LogLevel.Debug && l.Message.Contains("Channel was null"));
        Assert.DoesNotContain(capturedLogs, l => l.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task AckOrNackAsync_ChannelClosed_LogsDebug_NoFrameSent()
    {
        var capturedLogs = new List<CapturedLog>();
        var consumerChannel = BuildConsumerChannel(isOpen: false);
        var pipeline = new RabbitMqDispatchPipeline(
            "q", () => false, () => false, BuildCapturingLogger(capturedLogs));

        await pipeline.AckOrNackAsync(consumerChannel.Object, MakeArgs(deliveryTag: 32), processed: true);

        Assert.Contains(capturedLogs, l => l.Level == LogLevel.Debug && l.Message.Contains("Channel was closed"));
        consumerChannel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        consumerChannel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AckOrNackAsync_ShutdownTimedOut_LogsDebug_NoFrameSent()
    {
        var capturedLogs = new List<CapturedLog>();
        var consumerChannel = BuildConsumerChannel(isOpen: true);
        var pipeline = new RabbitMqDispatchPipeline(
            "q",
            shutdownTimedOutQuery: () => true,
            shutdownStartedQuery: () => true,
            BuildCapturingLogger(capturedLogs));

        await pipeline.AckOrNackAsync(consumerChannel.Object, MakeArgs(deliveryTag: 33), processed: true);

        Assert.Contains(capturedLogs, l => l.Level == LogLevel.Debug && l.Message.Contains("Shutdown grace window expired"));
        consumerChannel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AckOrNackAsync_AlreadyClosed_DuringShutdown_LogsDebug()
    {
        var capturedLogs = new List<CapturedLog>();
        var consumerChannel = new Mock<IChannel>(MockBehavior.Strict);
        consumerChannel.Setup(c => c.IsOpen).Returns(true);
        consumerChannel
            .Setup(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new global::RabbitMQ.Client.Exceptions.AlreadyClosedException(
                new ShutdownEventArgs(ShutdownInitiator.Application, 0, "test")));

        var pipeline = new RabbitMqDispatchPipeline(
            "q",
            shutdownTimedOutQuery: () => false,
            shutdownStartedQuery: () => true,  // we are shutting down
            BuildCapturingLogger(capturedLogs));

        await pipeline.AckOrNackAsync(consumerChannel.Object, MakeArgs(deliveryTag: 34), processed: true);

        Assert.Contains(capturedLogs,
            l => l.Level == LogLevel.Debug && l.Message.Contains("Channel already closed while acking/nacking"));
        // No Warning/Error: the shutdown-shaped failure is demoted to Debug.
        Assert.DoesNotContain(capturedLogs, l => l.Level == LogLevel.Warning);
        Assert.DoesNotContain(capturedLogs, l => l.Level == LogLevel.Error);
    }

    [Fact]
    public async Task AckOrNackAsync_AlreadyClosed_NotShutdown_RoutesViaLogAckOrNackFailure()
    {
        // Outside shutdown, AlreadyClosedException is unexpected → goes through
        // RabbitMqClientLog.AckFailed (processed=true) which logs at Warning.
        var capturedLogs = new List<CapturedLog>();
        var consumerChannel = new Mock<IChannel>(MockBehavior.Strict);
        consumerChannel.Setup(c => c.IsOpen).Returns(true);
        consumerChannel
            .Setup(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new global::RabbitMQ.Client.Exceptions.AlreadyClosedException(
                new ShutdownEventArgs(ShutdownInitiator.Application, 0, "test")));

        var pipeline = new RabbitMqDispatchPipeline(
            "q",
            shutdownTimedOutQuery: () => false,
            shutdownStartedQuery: () => false,  // NOT shutting down
            BuildCapturingLogger(capturedLogs));

        await pipeline.AckOrNackAsync(consumerChannel.Object, MakeArgs(deliveryTag: 35), processed: true);

        // The AckFailed log is at Warning (its severity is fixed by LoggerMessage.Define).
        Assert.Contains(capturedLogs, l => l.Level == LogLevel.Warning);
        // Not the "during shutdown" Debug path.
        Assert.DoesNotContain(capturedLogs,
            l => l.Level == LogLevel.Debug && l.Message.Contains("during shutdown"));
    }

    [Fact]
    public async Task AckOrNackAsync_ObjectDisposed_DuringShutdown_LogsDebug()
    {
        var capturedLogs = new List<CapturedLog>();
        var consumerChannel = new Mock<IChannel>(MockBehavior.Strict);
        consumerChannel.Setup(c => c.IsOpen).Returns(true);
        consumerChannel
            .Setup(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ObjectDisposedException("IChannel"));

        var pipeline = new RabbitMqDispatchPipeline(
            "q",
            shutdownTimedOutQuery: () => false,
            shutdownStartedQuery: () => true,
            BuildCapturingLogger(capturedLogs));

        await pipeline.AckOrNackAsync(consumerChannel.Object, MakeArgs(deliveryTag: 36), processed: true);

        Assert.Contains(capturedLogs,
            l => l.Level == LogLevel.Debug && l.Message.Contains("Channel disposed while acking/nacking"));
        Assert.DoesNotContain(capturedLogs, l => l.Level == LogLevel.Warning);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static Mock<IChannel> BuildConsumerChannel(bool isOpen)
    {
        var ch = new Mock<IChannel>(MockBehavior.Strict);
        ch.Setup(c => c.IsOpen).Returns(isOpen);
        if (isOpen)
        {
            ch.Setup(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Returns(ValueTask.CompletedTask);
            ch.Setup(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Returns(ValueTask.CompletedTask);
        }
        return ch;
    }

    private static Mock<IChannel> BuildPublishChannel(bool retryPublishThrows = false)
    {
        var ch = new Mock<IChannel>(MockBehavior.Loose);
        if (retryPublishThrows)
        {
            ch.Setup(c => c.BasicPublishAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                    It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new global::RabbitMQ.Client.Exceptions.AlreadyClosedException(
                    new ShutdownEventArgs(ShutdownInitiator.Application, 0, "test")));
        }
        else
        {
            ch.Setup(c => c.BasicPublishAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                    It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<CancellationToken>()))
                .Returns(ValueTask.CompletedTask);
        }
        return ch;
    }

    private static InboundMessageProcessor BuildProcessor(
        string queueName,
        ConsumerEventHandler handler,
        bool shutdownTimedOut = false)
    {
        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns(queueName);
        queueConfig.SetupGet(q => q.AuditingEnabled).Returns(false);
        queueConfig.SetupGet(q => q.AuditQueueName).Returns("audit");
        queueConfig.SetupGet(q => q.AuditRoutingKey).Returns(string.Empty);
        queueConfig.SetupGet(q => q.ErrorQueueName).Returns("err");
        queueConfig.SetupGet(q => q.DisableErrors).Returns(false);

        var auditPublisher = new MessageAuditPublisher(queueConfig.Object);
        var retryHandler = new MessageRetryHandler(maxRetries: 3, errorExchange: "err", consumerQueueName: queueName, NullLogger.Instance);

        return new InboundMessageProcessor(
            consumerEventHandler: handler,
            retryHandler: retryHandler,
            auditPublisher: auditPublisher,
            queueConfiguration: queueConfig.Object,
            timeProvider: TimeProvider.System,
            logger: NullLogger.Instance,
            retryQueueName: queueName + ".Retries",
            errorsDisabled: false,
            deadLetterUnhandledMessages: false,
            includeMachineNameInHeaders: false,
            shutdownTimedOut: () => shutdownTimedOut,
            shutdownPublishToken: () => CancellationToken.None);
    }

    private static ILogger BuildCapturingLogger(List<CapturedLog> capturedLogs)
    {
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        logger
            .Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
            {
                var level = (LogLevel)invocation.Arguments[0];
                var formatter = (Delegate)invocation.Arguments[4];
                var message = (string)formatter.DynamicInvoke(invocation.Arguments[2], invocation.Arguments[3])!;
                capturedLogs.Add(new CapturedLog(level, message));
            }));
        return logger.Object;
    }

    private static BasicDeliverEventArgs MakeArgs(ulong deliveryTag)
        => new(
            consumerTag: "ct",
            deliveryTag: deliveryTag,
            redelivered: false,
            exchange: "",
            routingKey: "q",
            properties: new BasicProperties
            {
                Headers = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [HeaderKeys.FullTypeName] = "Foo.Bar",
                },
            },
            body: new byte[] { 1 });

    private sealed record CapturedLog(LogLevel Level, string Message);
}
