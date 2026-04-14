using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

public class RabbitMqConsumerHostTests
{
    private static (Mock<IServiceConnectConnection>, Mock<IChannel>) MockConnection()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.BasicQosAsync(It.IsAny<uint>(), It.IsAny<ushort>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.Setup(c => c.BasicConsumeAsync(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(),
            It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<IDictionary<string, object?>?>(),
            It.IsAny<IAsyncBasicConsumer>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("tag");
        channel.Setup(c => c.QueueBindAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IDictionary<string, object?>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.Setup(c => c.QueueDeleteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0u);
        channel.Setup(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        channel.Setup(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);
        channel.Setup(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);

        var conn = new Mock<IServiceConnectConnection>();
        conn.Setup(c => c.CreateChannelAsync()).ReturnsAsync(channel.Object);
        return (conn, channel);
    }

    private static Mock<ITransportConfiguration> MakeTransportCfg(ushort prefetch = 10, bool autoDelete = false, bool disablePrefetch = false)
    {
        var cfg = new Mock<ITransportConfiguration>();
        cfg.SetupGet(c => c.MaxRetries).Returns(3);
        cfg.SetupGet(c => c.PrefetchCount).Returns(prefetch);
        var settings = new Dictionary<string, object>();
        if (autoDelete) settings[RabbitMQSettingKeys.AutoDelete] = true;
        if (disablePrefetch) settings[RabbitMQSettingKeys.DisablePrefetch] = true;
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
        return cfg;
    }

    [Fact]
    public async Task StartConsumingAsync_SetsBasicQos_WhenPrefetchEnabled()
    {
        var (conn, channel) = MockConnection();
        var tcfg = MakeTransportCfg(prefetch: 7);
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object, retry, audit, NullLogger.Instance);

        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        channel.Verify(c => c.BasicQosAsync(0, 7, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartConsumingAsync_SkipsBasicQos_WhenPrefetchDisabled()
    {
        var (conn, channel) = MockConnection();
        var tcfg = MakeTransportCfg(disablePrefetch: true);
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object, retry, audit, NullLogger.Instance);

        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        channel.Verify(c => c.BasicQosAsync(
            It.IsAny<uint>(), It.IsAny<ushort>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ConsumeMessageTypeAsync_BindsQueueToExchange()
    {
        var (conn, channel) = MockConnection();
        var tcfg = MakeTransportCfg();
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object, retry, audit, NullLogger.Instance);

        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");
        await host.ConsumeMessageTypeAsync("SomeMsg");

        channel.Verify(c => c.QueueBindAsync("q", "SomeMsg", string.Empty,
            It.IsAny<IDictionary<string, object?>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_DeletesRetryQueue_WhenAutoDelete()
    {
        var (conn, channel) = MockConnection();
        var tcfg = MakeTransportCfg(autoDelete: true);
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object, retry, audit, NullLogger.Instance);
        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        await host.DisposeAsync();

        channel.Verify(c => c.QueueDeleteAsync("q.Retries", false, false, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_SkipsRetryQueueDelete_WhenAutoDeleteFalse()
    {
        var (conn, channel) = MockConnection();
        var tcfg = MakeTransportCfg(autoDelete: false);
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object, retry, audit, NullLogger.Instance);
        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        await host.DisposeAsync();

        channel.Verify(c => c.QueueDeleteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DisposeAsync_SwallowsObjectDisposedException_OnQueueDelete()
    {
        var (conn, channel) = MockConnection();
        channel.Setup(c => c.QueueDeleteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ObjectDisposedException("channel"));
        var tcfg = MakeTransportCfg(autoDelete: true);
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object, retry, audit, NullLogger.Instance);
        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        var thrown = await Record.ExceptionAsync(() => host.DisposeAsync().AsTask());
        Assert.Null(thrown);
    }

    // ─── Inbound message-size enforcement (R-022) ───────────────────────────

    private static Mock<ITransportConfiguration> MakeTransportCfgWithMaxSize(long maxSize)
    {
        var cfg = new Mock<ITransportConfiguration>();
        cfg.SetupGet(c => c.MaxRetries).Returns(3);
        cfg.SetupGet(c => c.PrefetchCount).Returns((ushort)10);
        var settings = new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.MessageSize] = maxSize,
        };
        cfg.SetupGet(c => c.ClientSettings).Returns(settings);
        return cfg;
    }

    /// <summary>
    /// Delivers a synthetic message via the consumer's HandleBasicDeliverAsync.
    /// Returns true if the consumer event handler was invoked.
    /// </summary>
    private static async Task<bool> DeliverMessageAsync(
        RabbitMqConsumerHost host,
        byte[] body,
        Dictionary<string, object>? headers = null)
    {
        // Retrieve the private _consumer field via reflection.
        var consumerField = typeof(RabbitMqConsumerHost)
            .GetField("_consumer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var consumer = consumerField?.GetValue(host) as RabbitMQ.Client.Events.AsyncEventingBasicConsumer;
        if (consumer == null) throw new InvalidOperationException("_consumer field not found or host not started.");

        var props = new RabbitMQ.Client.BasicProperties();
        if (headers != null)
            foreach (var kvp in headers)
                (props.Headers ??= new Dictionary<string, object?>())[kvp.Key] = kvp.Value;

        await consumer.HandleBasicDeliverAsync(
            consumerTag: "tag",
            deliveryTag: 1,
            redelivered: false,
            exchange: "",
            routingKey: "q",
            properties: props,
            body: body,
            cancellationToken: default);

        return true;
    }

    [Fact]
    public async Task EventAsync_OversizedMessage_IsNacked_AndHandlerNotInvoked()
    {
        const long maxSize = 10L;
        var (conn, channel) = MockConnection();
        var tcfg = MakeTransportCfgWithMaxSize(maxSize);
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        bool handlerInvoked = false;
        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object, retry, audit, NullLogger.Instance);
        await host.StartConsumingAsync(
            (_, _, _, _) => { handlerInvoked = true; return Task.FromResult(new ConsumeEventResult { Success = true }); },
            "q");

        var oversized = new byte[maxSize + 1];
        var msgHeaders = new Dictionary<string, object> { [HeaderKeys.TypeName] = "SomeType" };
        await DeliverMessageAsync(host, oversized, msgHeaders);

        Assert.False(handlerInvoked, "Consumer event handler must not be called for oversized messages.");
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), false, true, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EventAsync_ExactLimitMessage_IsProcessed()
    {
        const long maxSize = 10L;
        var (conn, channel) = MockConnection();
        var tcfg = MakeTransportCfgWithMaxSize(maxSize);
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        bool handlerInvoked = false;
        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object, retry, audit, NullLogger.Instance);
        await host.StartConsumingAsync(
            (_, _, _, _) => { handlerInvoked = true; return Task.FromResult(new ConsumeEventResult { Success = true }); },
            "q");

        var exactSize = new byte[maxSize];
        var msgHeaders = new Dictionary<string, object> { [HeaderKeys.TypeName] = "SomeType" };
        await DeliverMessageAsync(host, exactSize, msgHeaders);

        Assert.True(handlerInvoked, "Consumer event handler must be called for messages within the limit.");
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
