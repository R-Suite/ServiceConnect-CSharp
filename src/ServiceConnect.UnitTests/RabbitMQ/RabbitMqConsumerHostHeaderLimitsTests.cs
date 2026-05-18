using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Wiring tests that confirm <see cref="RabbitMQSettingKeys.MaxHeaderCount"/> and
/// <see cref="RabbitMQSettingKeys.MaxHeaderValueBytes"/> reach the admission-time header
/// guards inside <see cref="RabbitMqConsumerHost"/>. Behaviour of the validator itself is
/// covered by <c>RabbitMqHeaderValidatorTests</c>; here we only verify that the configured
/// caps supplant the host's <c>DefaultMaxHeaderCount</c> (64) and
/// <c>DefaultMaxHeaderValueBytes</c> (8192) constants.
/// </summary>
public sealed class RabbitMqConsumerHostHeaderLimitsTests
{
    [Fact]
    public async Task ConfiguredMaxHeaderCount_AboveCap_IsRejected()
    {
        // MaxHeaderCount=5 → 6 headers (including TypeName) must NACK to error exchange.
        var (conn, channel, publishChannel) = MockConnection();
        var tcfg = MakeTransportCfgWithMaxHeaderCount(5);
        var qcfg = MakeQueueCfg();
        var bus = MakeBusCfg();
        var retry = new MessageRetryHandler(3, "err", "q", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        bool handlerInvoked = false;
        var host = new RabbitMqConsumerHost(
            conn.Object, tcfg.Object, qcfg.Object, bus.Object,
            retry, new RabbitMqAdmissionGate("q"), audit, NullLogger.Instance);
        await host.StartConsumingAsync(
            (_, _, _, _) => { handlerInvoked = true; return Task.FromResult(new ConsumeEventResult { Success = true }); },
            "q");

        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.TypeName] = "SomeType",
            ["X-1"] = "v",
            ["X-2"] = "v",
            ["X-3"] = "v",
            ["X-4"] = "v",
            ["X-5"] = "v",
        };

        await DeliverAsync(host, headers);

        Assert.False(handlerInvoked, "Handler must not be invoked when header count exceeds the configured cap.");
        publishChannel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, true,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfiguredMaxHeaderCount_AtCap_IsAdmitted()
    {
        // MaxHeaderCount=5 → 5 headers (including TypeName) must reach the handler.
        var (conn, _, publishChannel) = MockConnection();
        var tcfg = MakeTransportCfgWithMaxHeaderCount(5);
        var qcfg = MakeQueueCfg();
        var bus = MakeBusCfg();
        var retry = new MessageRetryHandler(3, "err", "q", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        bool handlerInvoked = false;
        var host = new RabbitMqConsumerHost(
            conn.Object, tcfg.Object, qcfg.Object, bus.Object,
            retry, new RabbitMqAdmissionGate("q"), audit, NullLogger.Instance);
        await host.StartConsumingAsync(
            (_, _, _, _) => { handlerInvoked = true; return Task.FromResult(new ConsumeEventResult { Success = true }); },
            "q");

        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.TypeName] = "SomeType",
            ["X-1"] = "v",
            ["X-2"] = "v",
            ["X-3"] = "v",
            ["X-4"] = "v",
        };

        await DeliverAsync(host, headers);

        Assert.True(handlerInvoked, "Handler must be invoked when header count is at the configured cap.");
        // No terminal-failure publish should have happened for this admitted delivery.
        publishChannel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, true,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnsetMaxHeaderCount_DefaultsToSixtyFour_AndAdmitsExactlySixtyFour()
    {
        // No MaxHeaderCount setting → default of 64 still applies (DefaultMaxHeaderCount).
        // 64 headers (including TypeName) must pass admission.
        var (conn, _, publishChannel) = MockConnection();
        var tcfg = MakeTransportCfgWithMaxHeaderCount(null);
        var qcfg = MakeQueueCfg();
        var bus = MakeBusCfg();
        var retry = new MessageRetryHandler(3, "err", "q", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        bool handlerInvoked = false;
        var host = new RabbitMqConsumerHost(
            conn.Object, tcfg.Object, qcfg.Object, bus.Object,
            retry, new RabbitMqAdmissionGate("q"), audit, NullLogger.Instance);
        await host.StartConsumingAsync(
            (_, _, _, _) => { handlerInvoked = true; return Task.FromResult(new ConsumeEventResult { Success = true }); },
            "q");

        var headers = new Dictionary<string, object> { [HeaderKeys.TypeName] = "SomeType" };
        for (int i = 0; i < 63; i++)
        {
            headers[$"X-{i}"] = "v";
        }

        await DeliverAsync(host, headers);

        Assert.True(handlerInvoked, "Handler must be invoked at the default cap of 64 when no override is configured.");
        publishChannel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, true,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfiguredMaxHeaderValueBytes_AboveCap_IsRejected()
    {
        // MaxHeaderValueBytes=16 → a 17-byte header value must NACK to the error exchange.
        var (conn, channel, publishChannel) = MockConnection();
        var tcfg = MakeTransportCfgWithMaxHeaderValueBytes(16);
        var qcfg = MakeQueueCfg();
        var bus = MakeBusCfg();
        var retry = new MessageRetryHandler(3, "err", "q", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        bool handlerInvoked = false;
        var host = new RabbitMqConsumerHost(
            conn.Object, tcfg.Object, qcfg.Object, bus.Object,
            retry, new RabbitMqAdmissionGate("q"), audit, NullLogger.Instance);
        await host.StartConsumingAsync(
            (_, _, _, _) => { handlerInvoked = true; return Task.FromResult(new ConsumeEventResult { Success = true }); },
            "q");

        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.TypeName] = "SomeType",
            ["X-Big"] = new string('a', 17),
        };

        await DeliverAsync(host, headers);

        Assert.False(handlerInvoked, "Handler must not be invoked when a header value exceeds the configured byte cap.");
        publishChannel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, true,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfiguredMaxHeaderValueBytes_AtCap_IsAdmitted()
    {
        // MaxHeaderValueBytes=16 → a 16-byte header value must reach the handler.
        var (conn, _, publishChannel) = MockConnection();
        var tcfg = MakeTransportCfgWithMaxHeaderValueBytes(16);
        var qcfg = MakeQueueCfg();
        var bus = MakeBusCfg();
        var retry = new MessageRetryHandler(3, "err", "q", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        bool handlerInvoked = false;
        var host = new RabbitMqConsumerHost(
            conn.Object, tcfg.Object, qcfg.Object, bus.Object,
            retry, new RabbitMqAdmissionGate("q"), audit, NullLogger.Instance);
        await host.StartConsumingAsync(
            (_, _, _, _) => { handlerInvoked = true; return Task.FromResult(new ConsumeEventResult { Success = true }); },
            "q");

        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.TypeName] = "SomeType",
            ["X-Big"] = new string('a', 16),
        };

        await DeliverAsync(host, headers);

        Assert.True(handlerInvoked, "Handler must be invoked when a header value is at the configured byte cap.");
        publishChannel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, true,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnsetMaxHeaderValueBytes_DefaultsToEightKb_AndAdmitsExactlyEightKb()
    {
        // No MaxHeaderValueBytes setting → default of 8192 still applies. An 8192-byte
        // header value must pass admission.
        var (conn, _, publishChannel) = MockConnection();
        var tcfg = MakeTransportCfgWithMaxHeaderValueBytes(null);
        var qcfg = MakeQueueCfg();
        var bus = MakeBusCfg();
        var retry = new MessageRetryHandler(3, "err", "q", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        bool handlerInvoked = false;
        var host = new RabbitMqConsumerHost(
            conn.Object, tcfg.Object, qcfg.Object, bus.Object,
            retry, new RabbitMqAdmissionGate("q"), audit, NullLogger.Instance);
        await host.StartConsumingAsync(
            (_, _, _, _) => { handlerInvoked = true; return Task.FromResult(new ConsumeEventResult { Success = true }); },
            "q");

        var headers = new Dictionary<string, object>
        {
            [HeaderKeys.TypeName] = "SomeType",
            ["X-Big"] = new string('a', 8192),
        };

        await DeliverAsync(host, headers);

        Assert.True(handlerInvoked, "Handler must be invoked at the default 8 KB value cap when no override is configured.");
        publishChannel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, true,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Harness — mirrors the helpers in RabbitMqConsumerHostTests but parameterises
    //               MaxHeaderCount and MaxHeaderValueBytes through ClientSettings.

    private static (Mock<IServiceConnectConnection> Connection, Mock<IChannel> ConsumerChannel, Mock<IChannel> PublishChannel) MockConnection()
    {
        var consumerChannel = CreateMockChannel();
        var publishChannel = CreateMockChannel();
        var conn = new Mock<IServiceConnectConnection>();
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>())).ReturnsAsync(consumerChannel.Object);
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(publishChannel.Object);
        return (conn, consumerChannel, publishChannel);
    }

    private static Mock<IChannel> CreateMockChannel()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.BasicQosAsync(It.IsAny<uint>(), It.IsAny<ushort>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        channel.Setup(c => c.BasicConsumeAsync(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(),
            It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<IDictionary<string, object?>?>(),
            It.IsAny<IAsyncBasicConsumer>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("tag");
        channel.Setup(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);
        channel.Setup(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);
        channel.Setup(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        channel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return channel;
    }

    private static Mock<ITransportConfiguration> MakeTransportCfgWithMaxHeaderCount(int? maxHeaderCount)
        => MakeTransportCfg(maxHeaderCount, maxHeaderValueBytes: null);

    private static Mock<ITransportConfiguration> MakeTransportCfgWithMaxHeaderValueBytes(int? maxHeaderValueBytes)
        => MakeTransportCfg(maxHeaderCount: null, maxHeaderValueBytes);

    private static Mock<ITransportConfiguration> MakeTransportCfg(int? maxHeaderCount, int? maxHeaderValueBytes)
    {
        var cfg = new Mock<ITransportConfiguration>();
        cfg.SetupGet(c => c.MaxRetries).Returns(3);
        cfg.SetupGet(c => c.PrefetchCount).Returns((ushort)10);
        cfg.SetupProperty(c => c.GracefulShutdownTimeoutMilliseconds, 5000);
        var settings = new Dictionary<string, object>();
        if (maxHeaderCount.HasValue)
        {
            settings[RabbitMQSettingKeys.MaxHeaderCount] = maxHeaderCount.Value;
        }

        if (maxHeaderValueBytes.HasValue)
        {
            settings[RabbitMQSettingKeys.MaxHeaderValueBytes] = maxHeaderValueBytes.Value;
        }

        cfg.SetupGet(c => c.ClientSettings).Returns(settings);
        return cfg;
    }

    private static Mock<IQueueConfiguration> MakeQueueCfg()
    {
        var cfg = new Mock<IQueueConfiguration>();
        cfg.SetupGet(c => c.QueueName).Returns("q");
        cfg.SetupGet(c => c.ErrorQueueName).Returns("err");
        cfg.SetupGet(c => c.AuditQueueName).Returns("audit");
        cfg.SetupGet(c => c.DisableErrors).Returns(false);
        cfg.SetupGet(c => c.AuditingEnabled).Returns(false);
        return cfg;
    }

    private static Mock<IBusConfiguration> MakeBusCfg()
    {
        var cfg = new Mock<IBusConfiguration>();
        cfg.SetupGet(c => c.IncludeMachineNameInHeaders).Returns(false);
        cfg.SetupGet(c => c.DeadLetterUnhandledMessages).Returns(false);
        return cfg;
    }

    private static async Task DeliverAsync(RabbitMqConsumerHost host, Dictionary<string, object> headers)
    {
        var consumerField = typeof(RabbitMqConsumerHost)
            .GetField("_consumer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (consumerField?.GetValue(host) is not global::RabbitMQ.Client.Events.AsyncEventingBasicConsumer consumer)
        {
            throw new InvalidOperationException("_consumer field not found or host not started.");
        }

        var props = new BasicProperties();
        foreach (var kvp in headers)
        {
            (props.Headers ??= new Dictionary<string, object?>())[kvp.Key] = kvp.Value;
        }

        await consumer.HandleBasicDeliverAsync(
            consumerTag: "tag",
            deliveryTag: 1,
            redelivered: false,
            exchange: "",
            routingKey: "q",
            properties: props,
            body: new byte[] { 1 },
            cancellationToken: default);
    }
}
