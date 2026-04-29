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
/// Verifies that the header-size admission guard in RabbitMqConsumerHost rejects
/// oversized string header values in the same way it already rejects oversized byte[]
/// values (M15 fix). Tests drive deliveries through the internal RaiseDeliveryForTests
/// seam rather than going through the broker.
///
/// This is the first test class to exercise EventAsync directly. BuildHostAsync
/// establishes the harness pattern that Tasks 7/8/9 should copy.
/// </summary>
public sealed class RabbitMqConsumerHostHeaderSizeTests
{
    // DefaultMaxHeaderValueBytes is 8192 in the host; use 9000 chars to exceed it.
    private const int OverLimitStringLength = 9000;

    [Fact]
    public async Task EventAsync_LargeStringHeader_RoutesToTerminalFailure()
    {
        var (host, _, capturedExceptions) = await BuildHostAsync();

        var args = MakeArgs(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [HeaderKeys.FullTypeName] = "Foo.Bar",
            ["X-Large"] = new string('x', OverLimitStringLength),
        });

        await host.RaiseDeliveryForTests(args);

        // The terminal-failure path publishes to the error exchange; the Exception header
        // JSON is serialised by MessageRetryHandler and captured via BasicPublishAsync.
        var ex = Assert.Single(capturedExceptions);
        Assert.Contains("X-Large", ex);
        // UTF-8 byte count of ASCII chars equals the char count, so 9000 appears in the message.
        Assert.Contains(OverLimitStringLength.ToString(System.Globalization.CultureInfo.InvariantCulture), ex);
    }

    [Fact]
    public async Task EventAsync_SmallStringHeader_PassesAdmission()
    {
        var (host, _, capturedExceptions) = await BuildHostAsync();

        var args = MakeArgs(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [HeaderKeys.FullTypeName] = "Foo.Bar",
            ["X-Small"] = "small",
        });

        await host.RaiseDeliveryForTests(args);

        // No size-violation exception should have been published for X-Small.
        // (The delivery may still fail for other reasons — no real handler is wired —
        //  but it must NOT fail due to the header-size guard.)
        Assert.DoesNotContain(capturedExceptions, e => e.Contains("X-Small"));
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="RabbitMqConsumerHost"/> with mocked channels, calls
    /// <see cref="RabbitMqConsumerHost.StartConsumingAsync"/> so that _model and
    /// _publishChannel are populated, and returns the host together with the mocked
    /// publish channel and a live list that accumulates every Exception-header JSON
    /// snippet that BasicPublishAsync receives.
    ///
    /// Pattern for follow-on tasks (7/8/9): copy this method and add extra channel
    /// setup / capture points as needed. The key contracts are:
    ///   - conn.CreateChannelAsync(CT) → consumerChannel (for BasicQos + BasicConsume + BasicAck/Nack)
    ///   - conn.CreateChannelAsync(CreateChannelOptions?, CT) → publishChannel (for BasicPublishAsync)
    ///   - publishChannel captures all BasicPublishAsync calls into capturedExceptions
    /// </summary>
    private static async Task<(RabbitMqConsumerHost Host, Mock<IChannel> PublishChannel, List<string> CapturedExceptions)> BuildHostAsync()
    {
        var capturedExceptions = new List<string>();

        // ── Consumer channel (BasicQos + BasicConsume + BasicAck/Nack) ──────
        var consumerChannel = new Mock<IChannel>(MockBehavior.Strict);
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
        consumerChannel.Setup(c => c.BasicAckAsync(
                It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        consumerChannel.Setup(c => c.BasicNackAsync(
                It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        consumerChannel.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumerChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        // ChannelShutdownAsync event subscription (the host wires _consumer.ShutdownAsync and
        // _model.ChannelShutdownAsync; Moq needs the add/remove set up for strict mocks).
        consumerChannel.SetupAdd(c => c.ChannelShutdownAsync += It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());
        consumerChannel.SetupRemove(c => c.ChannelShutdownAsync -= It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());

        // ── Publish channel (BasicPublishAsync — captures exception JSON) ───
        var publishChannel = new Mock<IChannel>(MockBehavior.Loose);
        publishChannel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>(
                (_, _, _, props, _, _) =>
                {
                    if (props.Headers != null &&
                        props.Headers.TryGetValue(HeaderKeys.Exception, out var raw) &&
                        raw is not null)
                    {
                        // MessageRetryHandler stamps the Exception header as a JSON string
                        // via HeaderHelpers.SetHeader, which encodes it as a plain string.
                        // Decode: string arrives either as string or UTF-8 byte[].
                        var json = raw switch
                        {
                            string s => s,
                            byte[] b => System.Text.Encoding.UTF8.GetString(b),
                            _ => raw.ToString() ?? string.Empty,
                        };
                        capturedExceptions.Add(json);
                    }
                })
            .Returns(ValueTask.CompletedTask);
        publishChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        // ── Connection ──────────────────────────────────────────────────────
        var conn = new Mock<IServiceConnectConnection>();
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(consumerChannel.Object);
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(publishChannel.Object);
        // UnderlyingConnection is accessed in StartConsumingAsync to subscribe to
        // connection-level events. Return null so no IConnection event wiring is needed.
        conn.SetupGet(c => c.UnderlyingConnection).Returns((IConnection?)null);

        // ── Transport / queue / bus configuration ───────────────────────────
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.MaxRetries).Returns(3);
        transport.SetupGet(t => t.PrefetchCount).Returns((ushort)10);
        transport.SetupProperty(t => t.GracefulShutdownTimeoutMilliseconds, 5000);
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");
        queue.SetupGet(q => q.ErrorQueueName).Returns("err");
        queue.SetupGet(q => q.AuditQueueName).Returns("audit");
        queue.SetupGet(q => q.AuditRoutingKey).Returns(string.Empty);
        queue.SetupGet(q => q.DisableErrors).Returns(false);
        queue.SetupGet(q => q.AuditingEnabled).Returns(false);

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);
        bus.SetupGet(b => b.DeadLetterUnhandledMessages).Returns(false);

        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher(queue.Object);

        var host = new RabbitMqConsumerHost(
            conn.Object, transport.Object, queue.Object, bus.Object,
            retry, audit, NullLogger.Instance);

        // Wire up the consumer channel and publish channel by calling StartConsumingAsync.
        // A no-op handler is sufficient — we are testing the admission guard, not dispatch.
        await host.StartConsumingAsync(
            (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }),
            queueName: "q");

        return (host, publishChannel, capturedExceptions);
    }

    private static BasicDeliverEventArgs MakeArgs(IDictionary<string, object?>? headers = null)
        => new(
            consumerTag: "ct",
            deliveryTag: 1,
            redelivered: false,
            exchange: "",
            routingKey: "q",
            properties: new BasicProperties { Headers = headers },
            body: new byte[] { 1 });
}
