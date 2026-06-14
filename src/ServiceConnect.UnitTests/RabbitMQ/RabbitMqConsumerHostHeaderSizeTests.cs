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
/// The header-size admission guard in RabbitMqConsumerHost rejects oversized string
/// header values in the same way it already rejects oversized byte[] values. Tests
/// drive deliveries through the internal RaiseDeliveryForTests seam rather than
/// going through the broker. BuildHostAsync establishes the harness pattern reused
/// by other EventAsync-direct tests.
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

    [Fact]
    public async Task EventAsync_NestedDictionaryHeaderExceedingByteBudget_RoutesToTerminalFailure()
    {
        // 9 KB of payload nested inside an IDictionary header value — a single header value
        // that bypasses the per-value 8 KB cap by nesting. Pre-fix this passes the validator
        // because the size loop only inspects top-level byte[]/string. Post-fix the recursive
        // byte-cost descends into the dictionary and rejects.
        var (host, _, capturedExceptions) = await BuildHostAsync();

        var nested = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["inner"] = new string('x', OverLimitStringLength),
        };
        var args = MakeArgs(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [HeaderKeys.FullTypeName] = "Foo.Bar",
            ["X-NestedTable"] = nested,
        });

        await host.RaiseDeliveryForTests(args);

        var ex = Assert.Single(capturedExceptions);
        Assert.Contains("X-NestedTable", ex);
    }

    [Fact]
    public async Task EventAsync_NestedListHeaderExceedingByteBudget_RoutesToTerminalFailure()
    {
        // Same shape but using IList (AMQP array).
        var (host, _, capturedExceptions) = await BuildHostAsync();

        var nested = new List<object?>
        {
            new string('x', OverLimitStringLength),
        };
        var args = MakeArgs(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [HeaderKeys.FullTypeName] = "Foo.Bar",
            ["X-NestedArray"] = nested,
        });

        await host.RaiseDeliveryForTests(args);

        var ex = Assert.Single(capturedExceptions);
        Assert.Contains("X-NestedArray", ex);
    }

    [Fact]
    public async Task EventAsync_NestedDictionaryHeaderUnderByteBudget_PassesAdmission()
    {
        // Nested table whose total payload is well under the 8 KB cap — must NOT be rejected.
        var (host, _, capturedExceptions) = await BuildHostAsync();

        var nested = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["inner1"] = "hello",
            ["inner2"] = new string('y', 1024),  // 1 KB
        };
        var args = MakeArgs(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [HeaderKeys.FullTypeName] = "Foo.Bar",
            ["X-SmallNested"] = nested,
        });

        await host.RaiseDeliveryForTests(args);

        Assert.DoesNotContain(capturedExceptions, e => e.Contains("X-SmallNested"));
    }

    [Fact]
    public async Task EventAsync_PathologicallyDeepNestedHeader_RoutesToTerminalFailure()
    {
        // Build 33 levels of single-entry-dict nesting — each level has a tiny payload, well
        // under the 8 KB budget on byte count alone. The depth guard rejects pathologically
        // deep payloads independently of byte count to prevent stack exhaustion if the byte
        // budget is ever widened.
        var (host, _, capturedExceptions) = await BuildHostAsync();

        object current = "leaf";
        for (var i = 0; i < 33; i++)
        {
            current = new Dictionary<string, object?>(StringComparer.Ordinal) { ["wrap"] = current };
        }

        var args = MakeArgs(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [HeaderKeys.FullTypeName] = "Foo.Bar",
            ["X-Deep"] = current,
        });

        await host.RaiseDeliveryForTests(args);

        var ex = Assert.Single(capturedExceptions);
        Assert.Contains("X-Deep", ex);
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
        queue.SetupGet(q => q.DisableErrors).Returns(false);
        queue.SetupGet(q => q.AuditingEnabled).Returns(false);

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);
        bus.SetupGet(b => b.DeadLetterUnhandledMessages).Returns(false);

        var retry = new MessageRetryHandler(3, "err", "q", NullLogger.Instance);
        var audit = new MessageAuditPublisher(queue.Object);

        var host = new RabbitMqConsumerHost(
            conn.Object, transport.Object, queue.Object, bus.Object,
            retry, new RabbitMqAdmissionGate("q"), audit, NullLogger.Instance);

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
