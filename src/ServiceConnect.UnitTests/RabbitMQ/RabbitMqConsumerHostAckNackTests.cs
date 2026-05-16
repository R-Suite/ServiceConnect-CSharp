using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// The ack/nack block in EventAsync's finally must demote expected-during-teardown
/// conditions to Debug rather than Warning:
///   - null channel captured at delivery entry
///   - channel already closed (IsOpen == false) at ack time
/// Uses the same harness pattern established by RabbitMqConsumerHostHeaderSizeTests.
/// </summary>
public sealed class RabbitMqConsumerHostAckNackTests
{
    [Fact]
    public async Task EventAsync_ChannelNullDuringFinally_LogsAtDebug()
    {
        var (host, _, _, capturedLogs) = await BuildHostAsync();

        // Null out _model via reflection so EventAsync's local `model` captures null.
        // Must happen BEFORE RaiseDeliveryForTests so EventAsync sees null at entry.
        var modelField = typeof(RabbitMqConsumerHost).GetField(
            "_model",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        modelField!.SetValue(host, null);

        var args = MakeArgs();
        await host.RaiseDeliveryForTests(args);

        Assert.Contains(capturedLogs, l => l.Level == LogLevel.Debug && l.Message.Contains("Channel was null"));
        Assert.DoesNotContain(capturedLogs, l => l.Level == LogLevel.Warning && l.Message.Contains("Channel was null"));
    }

    [Fact]
    public async Task EventAsync_ChannelClosedDuringFinally_LogsAtDebug_NoAckCalled()
    {
        var (host, consumerChannel, _, capturedLogs) = await BuildHostAsync(consumerChannelIsOpen: false);

        var args = MakeArgs();
        await host.RaiseDeliveryForTests(args);

        Assert.Contains(capturedLogs, l => l.Level == LogLevel.Debug && l.Message.Contains("Channel was closed"));
        consumerChannel.Verify(
            c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        consumerChannel.Verify(
            c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="RabbitMqConsumerHost"/> with mocked channels and a
    /// capturing <see cref="ILogger"/>. Copied from RabbitMqConsumerHostHeaderSizeTests
    /// and extended with:
    ///   - <paramref name="consumerChannelIsOpen"/> to drive the IsOpen pre-check
    ///   - captured log entries returned as the fourth tuple element
    /// </summary>
    private static async Task<(
        RabbitMqConsumerHost Host,
        Mock<IChannel> ConsumerChannel,
        Mock<IChannel> PublishChannel,
        List<CapturedLog> CapturedLogs)> BuildHostAsync(
        bool consumerChannelIsOpen = true)
    {
        var capturedLogs = new List<CapturedLog>();

        // ── Logger that captures all LogXxx calls ────────────────────────────
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

        // ── Consumer channel ─────────────────────────────────────────────────
        var consumerChannel = new Mock<IChannel>(MockBehavior.Strict);
        consumerChannel.Setup(c => c.IsOpen).Returns(consumerChannelIsOpen);
        consumerChannel.Setup(c => c.BasicQosAsync(
                It.IsAny<uint>(), It.IsAny<ushort>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumerChannel.Setup(c => c.BasicConsumeAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>?>(),
                It.IsAny<IAsyncBasicConsumer>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("tag");
        // BasicAckAsync / BasicNackAsync are NOT set up when consumerChannelIsOpen = false
        // so that the strict mock throws if either is called — confirming the IsOpen pre-check.
        if (consumerChannelIsOpen)
        {
            consumerChannel.Setup(c => c.BasicAckAsync(
                    It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Returns(ValueTask.CompletedTask);
            consumerChannel.Setup(c => c.BasicNackAsync(
                    It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Returns(ValueTask.CompletedTask);
        }

        consumerChannel.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumerChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        consumerChannel.SetupAdd(c => c.ChannelShutdownAsync += It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());
        consumerChannel.SetupRemove(c => c.ChannelShutdownAsync -= It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());

        // ── Publish channel ──────────────────────────────────────────────────
        var publishChannel = new Mock<IChannel>(MockBehavior.Loose);
        publishChannel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
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
        queue.SetupGet(q => q.QueueName).Returns("q");
        queue.SetupGet(q => q.ErrorQueueName).Returns("err");
        queue.SetupGet(q => q.AuditQueueName).Returns("audit");
        queue.SetupGet(q => q.DisableErrors).Returns(false);
        queue.SetupGet(q => q.AuditingEnabled).Returns(false);

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);
        bus.SetupGet(b => b.DeadLetterUnhandledMessages).Returns(false);

        var retry = new MessageRetryHandler(3, "err", "q", NullLogger.Instance);
        var audit = new MessageAuditPublisher(queue.Object);

        var host = new RabbitMqConsumerHost(
            conn.Object, transport.Object, queue.Object, bus.Object,
            retry, new RabbitMqAdmissionGate("q"), audit, logger.Object);

        await host.StartConsumingAsync(
            (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }),
            queueName: "q");

        return (host, consumerChannel, publishChannel, capturedLogs);
    }

    /// <summary>
    /// Builds a delivery with the minimal headers that get past the type-name
    /// admission guard (so <c>callbackAdmitted</c> becomes true and the finally
    /// ack/nack block is reached).
    /// </summary>
    private static BasicDeliverEventArgs MakeArgs()
        => new(
            consumerTag: "ct",
            deliveryTag: 1,
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
