using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class InboundMessageProcessorAuditCancellationTests
{
    private static BasicDeliverEventArgs MakeArgs()
    {
        var props = new BasicProperties();
        return new BasicDeliverEventArgs("tag", 1, false, "", "q", props, new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task ProcessAsync_AuditPublishOceDuringShutdown_SwallowsOceAndLogsAtDebug()
    {
        using var shutdownCts = new CancellationTokenSource();
        shutdownCts.Cancel();   // shutdown grace expired

        // Capture log calls via a simple list — ILogger (non-generic) can't be created as
        // NullLogger<T>, so we use a plain Mock and capture via callback.
        var logEntries = new List<(LogLevel Level, string Message)>();
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        loggerMock
            .Setup(l => l.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(inv =>
            {
                var level = (LogLevel)inv.Arguments[0];
                var formatter = (Delegate)inv.Arguments[4];
                var msg = (string)formatter.DynamicInvoke(inv.Arguments[2], inv.Arguments[3])!;
                logEntries.Add((level, msg));
            }));

        // Channel: BasicPublishAsync throws OCE carrying the shutdown token, simulating
        // a publish that fires after the shutdown grace window expires.
        var channelMock = new Mock<IChannel>();
        channelMock
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(shutdownCts.Token));

        // MessageAuditPublisher backed by the throwing channel, with auditing enabled.
        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.AuditingEnabled).Returns(true);
        queueConfig.SetupGet(q => q.AuditQueueName).Returns("audit");
        queueConfig.SetupGet(q => q.QueueName).Returns("q");
        var auditPublisher = new MessageAuditPublisher(queueConfig.Object);

        // Minimal retry handler — the success path skips it.
        var retryHandler = new MessageRetryHandler(
            maxRetries: 0,
            errorExchange: "err",
            consumerQueueName: "q",
            logger: loggerMock.Object);

        // Consumer event handler: returns Success=true, NotHandled=false so the audit
        // branch in ProcessAsync fires.
        static Task<ConsumeEventResult> HandleAsync(ReadOnlyMemory<byte> _, string __, IDictionary<string, object> ___, CancellationToken ____) =>
            Task.FromResult(new ConsumeEventResult { Success = true, NotHandled = false });

        var processor = new InboundMessageProcessor(
            consumerEventHandler: (ConsumerEventHandler)HandleAsync,
            retryHandler: retryHandler,
            auditPublisher: auditPublisher,
            queueConfiguration: queueConfig.Object,
            timeProvider: TimeProvider.System,
            logger: loggerMock.Object,
            retryQueueName: "q.retries",
            errorsDisabled: false,
            deadLetterUnhandledMessages: false,
            includeMachineNameInHeaders: false,
            shutdownTimedOut: () => false,
            shutdownPublishToken: () => shutdownCts.Token);

        // Corrected: the audit OCE is SWALLOWED — processor returns normally so the message is ack'd.
        var processed = await processor.ProcessAsync(channelMock.Object, MakeArgs(), copiedHeaders: null, CancellationToken.None);

        // Returns true (acks the original message).
        Assert.True(processed);

        // No Error logs — OCE during shutdown is expected, not an error.
        Assert.DoesNotContain(logEntries, e => e.Level == LogLevel.Error);
        // One Debug log mentioning the audit publish cancellation.
        Assert.Contains(logEntries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("Audit publish cancelled by shutdown"));
    }
}
