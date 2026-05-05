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

    // ── Task 8 — retry-publish transport discriminator ────────────────────────

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
            processor.ProcessAsync(channelMock.Object, MakeArgs(), CancellationToken.None));

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
            processor.ProcessAsync(channelMock.Object, MakeArgs(), CancellationToken.None));

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
        var processed = await processor.ProcessAsync(channelMock.Object, MakeArgs(), CancellationToken.None);
        Assert.True(processed);
    }

    // ── Task 9 — terminal-failure-publish transport discriminator ─────────────

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
            processor.ProcessAsync(channelMock.Object, MakeArgs(), CancellationToken.None));

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

        var processed = await processor.ProcessAsync(channelMock.Object, MakeArgs(), CancellationToken.None);
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
            processor.ProcessAsync(channelMock.Object, MakeArgs(), CancellationToken.None));

        Assert.Same(transportException, thrown);
    }
}
