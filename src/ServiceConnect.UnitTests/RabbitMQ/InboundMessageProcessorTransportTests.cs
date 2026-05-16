using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class InboundMessageProcessorTransportTests
{
    private static BasicDeliverEventArgs MakeArgs()
    {
        var props = new BasicProperties();
        return new BasicDeliverEventArgs("tag", 1, false, "", "q", props, new byte[] { 1, 2, 3 });
    }

    /// <summary>
    /// Builds a processor whose handler signals a failure (Success=false) so the
    /// retry-publish branch fires, and configures the mock IChannel so that
    /// BasicPublishAsync throws the given exception.
    /// </summary>
    private static InboundMessageProcessor MakeRetryPublishProcessor(
        Mock<IChannel> channelMock,
        Exception toThrow,
        Mock<ILogger> loggerMock)
    {
        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.AuditingEnabled).Returns(false);
        queueConfig.SetupGet(q => q.AuditQueueName).Returns("audit");
        queueConfig.SetupGet(q => q.AuditRoutingKey).Returns(string.Empty);
        queueConfig.SetupGet(q => q.QueueName).Returns("q");

        channelMock
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(toThrow);

        var auditPublisher = new MessageAuditPublisher(queueConfig.Object);

        var retryHandler = new MessageRetryHandler(
            maxRetries: 3,
            errorExchange: "err",
            consumerQueueName: "q",
            logger: loggerMock.Object);

        // Handler returns Success=false to trigger the retry-publish branch.
        static Task<ConsumeEventResult> HandleAsync(ReadOnlyMemory<byte> _, string __, IDictionary<string, object> ___, CancellationToken ____) =>
            Task.FromResult(new ConsumeEventResult { Success = false });

        using var shutdownCts = new CancellationTokenSource(); // NOT cancelled — shutdown hasn't fired.

        return new InboundMessageProcessor(
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
            shutdownPublishToken: () => CancellationToken.None);
    }

    /// <summary>
    /// Builds a processor whose handler signals NotHandled=true so the
    /// terminal-failure-publish branch fires, and configures the mock IChannel so
    /// that BasicPublishAsync throws the given exception.
    /// </summary>
    private static InboundMessageProcessor MakeTerminalPublishProcessor(
        Mock<IChannel> channelMock,
        Exception toThrow,
        Mock<ILogger> loggerMock)
    {
        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.AuditingEnabled).Returns(false);
        queueConfig.SetupGet(q => q.AuditQueueName).Returns("audit");
        queueConfig.SetupGet(q => q.AuditRoutingKey).Returns(string.Empty);
        queueConfig.SetupGet(q => q.QueueName).Returns("q");

        channelMock
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(toThrow);

        var auditPublisher = new MessageAuditPublisher(queueConfig.Object);

        var retryHandler = new MessageRetryHandler(
            maxRetries: 0,
            errorExchange: "err",
            consumerQueueName: "q",
            logger: loggerMock.Object);

        // Handler returns Success=true, NotHandled=true to trigger the terminal-failure branch.
        static Task<ConsumeEventResult> HandleAsync(ReadOnlyMemory<byte> _, string __, IDictionary<string, object> ___, CancellationToken ____) =>
            Task.FromResult(new ConsumeEventResult { Success = true, NotHandled = true });

        return new InboundMessageProcessor(
            consumerEventHandler: (ConsumerEventHandler)HandleAsync,
            retryHandler: retryHandler,
            auditPublisher: auditPublisher,
            queueConfiguration: queueConfig.Object,
            timeProvider: TimeProvider.System,
            logger: loggerMock.Object,
            retryQueueName: "q.retries",
            errorsDisabled: false,
            deadLetterUnhandledMessages: true,
            includeMachineNameInHeaders: false,
            shutdownTimedOut: () => false,
            shutdownPublishToken: () => CancellationToken.None);
    }

    // ── Retry-publish transport discriminator ────────────────────────────────

    [Fact]
    public async Task ProcessAsync_RetryPublishThrowsAlreadyClosed_RethrowsForBrokerRedelivery()
    {
        var channelMock = new Mock<IChannel>();
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var transportException = new AlreadyClosedException(new ShutdownEventArgs(
            ShutdownInitiator.Peer, 0, "test"));

        var processor = MakeRetryPublishProcessor(channelMock, transportException, loggerMock);

        var thrown = await Assert.ThrowsAsync<AlreadyClosedException>(() =>
            processor.ProcessAsync(channelMock.Object, MakeArgs(), copiedHeaders: null, CancellationToken.None));

        Assert.Same(transportException, thrown);
    }

    [Fact]
    public async Task ProcessAsync_RetryPublishThrowsBrokerUnreachable_RethrowsForBrokerRedelivery()
    {
        var channelMock = new Mock<IChannel>();
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var transportException = new BrokerUnreachableException(new Exception("inner"));

        var processor = MakeRetryPublishProcessor(channelMock, transportException, loggerMock);

        var thrown = await Assert.ThrowsAsync<BrokerUnreachableException>(() =>
            processor.ProcessAsync(channelMock.Object, MakeArgs(), copiedHeaders: null, CancellationToken.None));

        Assert.Same(transportException, thrown);
    }

    [Fact]
    public async Task ProcessAsync_RetryPublishThrowsPoisonException_SwallowsAndAcks()
    {
        // Poison-message-style exception (anything not transport-class, not OCE) keeps
        // the existing swallow-and-ack behaviour to prevent a hot redelivery loop.
        var channelMock = new Mock<IChannel>();
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var poisonException = new InvalidOperationException("poison message");

        var processor = MakeRetryPublishProcessor(channelMock, poisonException, loggerMock);

        // Returns true (acks) — does not throw.
        var processed = await processor.ProcessAsync(channelMock.Object, MakeArgs(), copiedHeaders: null, CancellationToken.None);
        Assert.True(processed);
    }

    // ── Terminal-failure-publish transport discriminator ──────────────────────

    [Fact]
    public async Task ProcessAsync_TerminalFailurePublishThrowsAlreadyClosed_RethrowsForBrokerRedelivery()
    {
        var channelMock = new Mock<IChannel>();
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var transportException = new AlreadyClosedException(new ShutdownEventArgs(
            ShutdownInitiator.Peer, 0, "test"));

        var processor = MakeTerminalPublishProcessor(channelMock, transportException, loggerMock);

        var thrown = await Assert.ThrowsAsync<AlreadyClosedException>(() =>
            processor.ProcessAsync(channelMock.Object, MakeArgs(), copiedHeaders: null, CancellationToken.None));

        Assert.Same(transportException, thrown);
    }

    [Fact]
    public async Task ProcessAsync_TerminalFailurePublishThrowsPoisonException_SwallowsAndAcks()
    {
        var channelMock = new Mock<IChannel>();
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var poisonException = new InvalidOperationException("poison");

        var processor = MakeTerminalPublishProcessor(channelMock, poisonException, loggerMock);

        var processed = await processor.ProcessAsync(channelMock.Object, MakeArgs(), copiedHeaders: null, CancellationToken.None);
        Assert.True(processed);
    }

    [Fact]
    public async Task ProcessAsync_TerminalFailurePublishThrowsBrokerUnreachable_RethrowsForBrokerRedelivery()
    {
        var channelMock = new Mock<IChannel>();
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var transportException = new BrokerUnreachableException(new Exception("inner"));

        var processor = MakeTerminalPublishProcessor(channelMock, transportException, loggerMock);

        var thrown = await Assert.ThrowsAsync<BrokerUnreachableException>(() =>
            processor.ProcessAsync(channelMock.Object, MakeArgs(), copiedHeaders: null, CancellationToken.None));

        Assert.Same(transportException, thrown);
    }

    // ── OperationInterruptedException propagation (base type of AlreadyClosedException) ──

    [Fact]
    public async Task ProcessAsync_RetryPublishThrowsOperationInterrupted_RethrowsForBrokerRedelivery()
    {
        // OperationInterruptedException is the base class of AlreadyClosedException.
        // A plain base-type throw (e.g. broker-initiated 404/406) must propagate out
        // of ProcessAsync so the outer dispatch nacks-with-requeue; it must not be
        // swallowed by the generic catch and silently acked.
        var channelMock = new Mock<IChannel>();
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var transportException = new OperationInterruptedException(
            new ShutdownEventArgs(ShutdownInitiator.Library, 0, "test interruption"));

        var processor = MakeRetryPublishProcessor(channelMock, transportException, loggerMock);

        var thrown = await Assert.ThrowsAsync<OperationInterruptedException>(() =>
            processor.ProcessAsync(channelMock.Object, MakeArgs(), copiedHeaders: null, CancellationToken.None));

        Assert.Same(transportException, thrown);
    }

    [Fact]
    public async Task ProcessAsync_TerminalFailurePublishThrowsOperationInterrupted_RethrowsForBrokerRedelivery()
    {
        // Same invariant for the terminal-failure (NotHandled=true) path: a plain
        // OperationInterruptedException must propagate, not be swallowed and acked.
        var channelMock = new Mock<IChannel>();
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var transportException = new OperationInterruptedException(
            new ShutdownEventArgs(ShutdownInitiator.Library, 0, "test interruption"));

        var processor = MakeTerminalPublishProcessor(channelMock, transportException, loggerMock);

        var thrown = await Assert.ThrowsAsync<OperationInterruptedException>(() =>
            processor.ProcessAsync(channelMock.Object, MakeArgs(), copiedHeaders: null, CancellationToken.None));

        Assert.Same(transportException, thrown);
    }

    // ── Handler exception forwarded to DLQ (not retry-publish exception) ──────

    [Fact]
    public async Task ProcessAsync_RetryPublishFails_FallbackCarriesHandlerException_NotRetryException()
    {
        // When the retry-publish path throws, the fallback to the error exchange must stamp
        // the original handler exception (what the operator cares about) into the DLQ
        // Exception header, not the retry-publish exception that caused the reroute.
        var channelMock = new Mock<IChannel>();
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var handlerException = new InvalidOperationException("handler-failed-sentinel");
        var retryPublishException = new InvalidOperationException("retry-publish-failed-sentinel");

        // First BasicPublishAsync (retry path) throws; second (error-exchange fallback) succeeds.
        // Track captured properties on the second call to inspect the Exception header.
        BasicProperties? capturedProps = null;
        int callCount = 0;
        channelMock
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string _exchange, string _rk, bool _mandatory, BasicProperties props, ReadOnlyMemory<byte> _body, CancellationToken _ct) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return ValueTask.FromException(retryPublishException);
                }
                capturedProps = props;
                return ValueTask.CompletedTask;
            });

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.AuditingEnabled).Returns(false);
        queueConfig.SetupGet(q => q.AuditQueueName).Returns("audit");
        queueConfig.SetupGet(q => q.AuditRoutingKey).Returns(string.Empty);
        queueConfig.SetupGet(q => q.QueueName).Returns("q");

        var auditPublisher = new MessageAuditPublisher(queueConfig.Object);
        var retryHandler = new MessageRetryHandler(
            maxRetries: 3,
            errorExchange: "err",
            consumerQueueName: "q",
            logger: loggerMock.Object);

        // Handler throws the sentinel exception directly.
        Task<ConsumeEventResult> HandleAsync(ReadOnlyMemory<byte> _, string __, IDictionary<string, object> ___, CancellationToken ____) =>
            Task.FromException<ConsumeEventResult>(handlerException);

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
            shutdownPublishToken: () => CancellationToken.None);

        var processed = await processor.ProcessAsync(channelMock.Object, MakeArgs(), copiedHeaders: null, CancellationToken.None);

        // Fallback completed — message is acked.
        Assert.True(processed);

        // The second BasicPublishAsync call must have fired (the error-exchange fallback).
        Assert.Equal(2, callCount);
        Assert.NotNull(capturedProps);

        // The Exception header must contain the handler exception message, not the retry
        // exception, so the DLQ entry identifies the business-logic failure.
        var exceptionHeaderRaw = capturedProps!.Headers?["Exception"];
        Assert.NotNull(exceptionHeaderRaw);
        var exceptionJson = exceptionHeaderRaw is byte[] bytes
            ? System.Text.Encoding.UTF8.GetString(bytes)
            : exceptionHeaderRaw as string;
        Assert.NotNull(exceptionJson);
        Assert.Contains("handler-failed-sentinel", exceptionJson, StringComparison.Ordinal);
        Assert.DoesNotContain("retry-publish-failed-sentinel", exceptionJson, StringComparison.Ordinal);
    }
}
