using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies the AckFailed (EventId 6) and NackFailed (EventId 7) source-gen
/// log entries on <see cref="RabbitMqClientLog"/>:
///   - AckFailed fires when the channel rejects a BasicAckAsync (handler succeeded
///     so processed=true). Must include MessageId, DeliveryTag, and queue.
///   - NackFailed fires when the channel rejects a BasicNackAsync (handler
///     failed/retry so processed=false).
///   - The shutdown-noise Debug branches in the catch blocks stay unchanged —
///     <see cref="RabbitMqConsumerHostAckNackTests"/> covers those.
///   - When BasicProperties.MessageId is unset, the log falls back to
///     DeliveryTag-as-string so the entry still pinpoints a delivery.
/// </summary>
public sealed class AckNackFailureLogsTests
{
    private const string TestQueueName = "q";

    [Fact]
    public async Task AckFailure_OnHandlerSuccess_EmitsAckFailed_WithMessageId()
    {
        var (host, _, fakeLogger) = await BuildHostAsync(
            handlerSuccess: true,
            ackThrows: new global::RabbitMQ.Client.Exceptions.AlreadyClosedException(
                new ShutdownEventArgs(ShutdownInitiator.Peer, 0, "broker reset")));

        var args = MakeArgs(messageId: "msg-7");
        await host.RaiseDeliveryForTests(args);

        var record = Assert.Single(
            fakeLogger.Collector.GetSnapshot(),
            r => r.Id.Id == RabbitMqClientLog.AckFailedEventId);
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains("msg-7", record.Message);
        Assert.Contains(TestQueueName, record.Message);
        // Sanity: DeliveryTag also appears in the message template.
        Assert.Contains("42", record.Message);
        // No NackFailed entry — handler succeeded.
        Assert.DoesNotContain(
            fakeLogger.Collector.GetSnapshot(),
            r => r.Id.Id == RabbitMqClientLog.NackFailedEventId);
    }

    [Fact]
    public async Task NackFailure_OnHandlerFailure_EmitsNackFailed_WithMessageId()
    {
        // Path: handler returns Success=false → InboundMessageProcessor.HandleFailureAsync
        // attempts a retry publish. Make the publish channel throw AlreadyClosedException
        // (in the rethrow allow-list of the processor) — that bubbles up, the EventAsync
        // catch sets processed=false, and the finally nacks. The nack throws too, so we
        // hit LogAckOrNackFailure with processed=false → NackFailed.
        var (host, _, fakeLogger) = await BuildHostAsync(
            handlerSuccess: false,
            publishThrows: new global::RabbitMQ.Client.Exceptions.AlreadyClosedException(
                new ShutdownEventArgs(ShutdownInitiator.Peer, 0, "broker reset")),
            nackThrows: new global::RabbitMQ.Client.Exceptions.AlreadyClosedException(
                new ShutdownEventArgs(ShutdownInitiator.Peer, 0, "broker reset")));

        var args = MakeArgs(messageId: "msg-9");
        await host.RaiseDeliveryForTests(args);

        var record = Assert.Single(
            fakeLogger.Collector.GetSnapshot(),
            r => r.Id.Id == RabbitMqClientLog.NackFailedEventId);
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains("msg-9", record.Message);
        Assert.Contains(TestQueueName, record.Message);
        Assert.DoesNotContain(
            fakeLogger.Collector.GetSnapshot(),
            r => r.Id.Id == RabbitMqClientLog.AckFailedEventId);
    }

    [Fact]
    public async Task AckFailure_WithoutMessageId_FallsBackToDeliveryTag()
    {
        var (host, _, fakeLogger) = await BuildHostAsync(
            handlerSuccess: true,
            ackThrows: new global::RabbitMQ.Client.Exceptions.AlreadyClosedException(
                new ShutdownEventArgs(ShutdownInitiator.Peer, 0, "broker reset")));

        var args = MakeArgs(messageId: null);
        await host.RaiseDeliveryForTests(args);

        var record = Assert.Single(
            fakeLogger.Collector.GetSnapshot(),
            r => r.Id.Id == RabbitMqClientLog.AckFailedEventId);
        // Producer didn't stamp MessageId; the log falls back to the DeliveryTag string.
        Assert.Contains("42", record.Message);
    }

    [Fact]
    public async Task AckFailure_OnGenericException_StillRoutesToAckFailed()
    {
        // The catch-all (catch (Exception ex)) routes through the same helper, so a
        // non-AlreadyClosed/non-ObjectDisposed exception should still emit AckFailed
        // when processed=true.
        var (host, _, fakeLogger) = await BuildHostAsync(
            handlerSuccess: true,
            ackThrows: new InvalidOperationException("synthetic ack failure"));

        var args = MakeArgs(messageId: "msg-x");
        await host.RaiseDeliveryForTests(args);

        var record = Assert.Single(
            fakeLogger.Collector.GetSnapshot(),
            r => r.Id.Id == RabbitMqClientLog.AckFailedEventId);
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains("msg-x", record.Message);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="RabbitMqConsumerHost"/> wired to a strict-ish consumer-channel
    /// mock whose BasicAckAsync / BasicNackAsync can be configured to throw. The handler
    /// returns success/failure per <paramref name="handlerSuccess"/> so the caller can
    /// drive both the ack-path and nack-path branches of EventAsync's finally block.
    /// </summary>
    private static async Task<(
        RabbitMqConsumerHost Host,
        Mock<IChannel> ConsumerChannel,
        FakeLogger<AckNackFailureTag> FakeLogger)> BuildHostAsync(
        bool handlerSuccess,
        Exception? ackThrows = null,
        Exception? nackThrows = null,
        Exception? publishThrows = null)
    {
        var fakeLogger = new FakeLogger<AckNackFailureTag>();

        // ── Consumer channel ─────────────────────────────────────────────────
        var consumerChannel = new Mock<IChannel>(MockBehavior.Loose);
        consumerChannel.Setup(c => c.IsOpen).Returns(true);
        consumerChannel.Setup(c => c.BasicQosAsync(
                It.IsAny<uint>(), It.IsAny<ushort>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumerChannel.Setup(c => c.BasicConsumeAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<IAsyncBasicConsumer>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("tag");
        var ackSetup = consumerChannel.Setup(c => c.BasicAckAsync(
            It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()));
        if (ackThrows is not null)
        {
            ackSetup.ThrowsAsync(ackThrows);
        }
        else
        {
            ackSetup.Returns(ValueTask.CompletedTask);
        }
        var nackSetup = consumerChannel.Setup(c => c.BasicNackAsync(
            It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()));
        if (nackThrows is not null)
        {
            nackSetup.ThrowsAsync(nackThrows);
        }
        else
        {
            nackSetup.Returns(ValueTask.CompletedTask);
        }
        consumerChannel.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumerChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        consumerChannel.SetupAdd(c => c.ChannelShutdownAsync += It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());
        consumerChannel.SetupRemove(c => c.ChannelShutdownAsync -= It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());

        // ── Publish channel ──────────────────────────────────────────────────
        var publishChannel = new Mock<IChannel>(MockBehavior.Loose);
        var publishSetup = publishChannel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()));
        if (publishThrows is not null)
        {
            publishSetup.ThrowsAsync(publishThrows);
        }
        else
        {
            publishSetup.Returns(ValueTask.CompletedTask);
        }
        publishChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        // ── Connection ───────────────────────────────────────────────────────
        var conn = new Mock<IServiceConnectConnection>();
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(consumerChannel.Object);
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(publishChannel.Object);
        conn.SetupGet(c => c.UnderlyingConnection).Returns((IConnection?)null);

        // ── Transport / queue / bus configuration ────────────────────────────
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.MaxRetries).Returns(3);
        transport.SetupGet(t => t.PrefetchCount).Returns((ushort)10);
        transport.SetupProperty(t => t.GracefulShutdownTimeoutMilliseconds, 5000);
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns(TestQueueName);
        queue.SetupGet(q => q.ErrorQueueName).Returns("err");
        queue.SetupGet(q => q.AuditQueueName).Returns("audit");
        queue.SetupGet(q => q.AuditRoutingKey).Returns(string.Empty);
        queue.SetupGet(q => q.DisableErrors).Returns(false);
        queue.SetupGet(q => q.AuditingEnabled).Returns(false);

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);
        bus.SetupGet(b => b.DeadLetterUnhandledMessages).Returns(false);

        var retry = new MessageRetryHandler(3, "err", TestQueueName, NullLogger.Instance);
        var audit = new MessageAuditPublisher(queue.Object);

        var host = new RabbitMqConsumerHost(
            conn.Object, transport.Object, queue.Object, bus.Object,
            retry, audit, fakeLogger);

        await host.StartConsumingAsync(
            (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = handlerSuccess }),
            queueName: TestQueueName);

        return (host, consumerChannel, fakeLogger);
    }

    /// <summary>
    /// Builds a delivery with the minimal headers that get past the type-name
    /// admission guard and lets the caller pin <c>MessageId</c>. DeliveryTag is
    /// fixed at 42 so the fallback-to-DeliveryTag test can pin it precisely.
    /// </summary>
    private static BasicDeliverEventArgs MakeArgs(string? messageId)
    {
        var properties = new BasicProperties
        {
            Headers = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [HeaderKeys.FullTypeName] = "Foo.Bar",
            },
        };
        if (messageId is not null)
        {
            properties.MessageId = messageId;
        }

        return new BasicDeliverEventArgs(
            consumerTag: "ct",
            deliveryTag: 42,
            redelivered: false,
            exchange: "",
            routingKey: TestQueueName,
            properties: properties,
            body: new byte[] { 1 });
    }

    /// <summary>Placeholder type so FakeLogger has a category — the actual ILogger
    /// passed to the host is the non-generic <see cref="ILogger"/>.</summary>
    public sealed class AckNackFailureTag { }
}
