using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

public class RabbitMqConsumerHostTests
{
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
        channel.Setup(c => c.BasicCancelAsync(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.Setup(c => c.QueueBindAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IDictionary<string, object?>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.Setup(c => c.QueueDeleteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0u);
        channel.Setup(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        channel.Setup(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);
        channel.Setup(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);
        return channel;
    }

    // Returns the consumer channel (the one bound to the AsyncEventingBasicConsumer via
    // BasicConsumeAsync). Tests assert on this mock for ack/nack + consumer lifecycle.
    // The host also opens a second publish channel for retry/audit/error publishes;
    // that mock is available from the returned tuple if a test needs to reason about it.
    private static (Mock<IServiceConnectConnection> Connection, Mock<IChannel> ConsumerChannel, Mock<IChannel> PublishChannel) MockConnection()
    {
        var consumerChannel = CreateMockChannel();
        var publishChannel = CreateMockChannel();
        var conn = new Mock<IServiceConnectConnection>();
        // Consumer channel is opened via the parameterless overload; the helper publish
        // channel is opened via the options overload so it can enable publisher confirms.
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>())).ReturnsAsync(consumerChannel.Object);
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(publishChannel.Object);
        return (conn, consumerChannel, publishChannel);
    }

    private static Mock<ITransportConfiguration> MakeTransportCfg(ushort prefetch = 10, bool autoDelete = false, bool disablePrefetch = false)
    {
        return MakeTransportCfg(prefetch, autoDelete, disablePrefetch, null);
    }

    private static Mock<ITransportConfiguration> MakeTransportCfg(
        ushort prefetch,
        bool autoDelete,
        bool disablePrefetch,
        int? gracefulShutdownTimeoutMs)
    {
        var cfg = new Mock<ITransportConfiguration>();
        cfg.SetupGet(c => c.MaxRetries).Returns(3);
        cfg.SetupGet(c => c.PrefetchCount).Returns(prefetch);
        cfg.SetupProperty(c => c.GracefulShutdownTimeoutMilliseconds, gracefulShutdownTimeoutMs ?? 5000);
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
        var (conn, channel, _) = MockConnection();
        var tcfg = MakeTransportCfg(prefetch: 7);
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object, retry, audit, NullLogger.Instance);

        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        channel.Verify(c => c.BasicQosAsync(0, 7, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData((ushort)5, (ushort)5)]
    [InlineData((long)7L, (ushort)7)]
    [InlineData("9", (ushort)9)]
    public async Task StartConsumingAsync_AcceptsNonIntPrefetchSettingOverride(object settingValue, ushort expected)
    {
        // The prefetch override must accept every boxed shape that configuration
        // providers emit (ushort, long, numeric string, etc.). A hard cast to int
        // before Convert.ToUInt16 would throw InvalidCastException on valid input.
        var (conn, channel, _) = MockConnection();
        var tcfg = new Mock<ITransportConfiguration>();
        tcfg.SetupGet(c => c.MaxRetries).Returns(3);
        tcfg.SetupGet(c => c.PrefetchCount).Returns((ushort)1);
        tcfg.SetupProperty(c => c.GracefulShutdownTimeoutMilliseconds, 5000);
        var settings = new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.PrefetchCount] = settingValue
        };
        tcfg.SetupGet(c => c.ClientSettings).Returns(settings);

        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object, retry, audit, NullLogger.Instance);

        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        channel.Verify(c => c.BasicQosAsync(0, expected, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartConsumingAsync_SkipsBasicQos_WhenPrefetchDisabled()
    {
        var (conn, channel, _) = MockConnection();
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
    public async Task StartConsumingAsync_CreatesPublishChannel_WithPublisherConfirmsEnabled()
    {
        // The helper channel carries retry/audit/error publishes. Without publisher
        // confirms + tracking, BasicPublishAsync returns before the broker acks, so a
        // lost helper publish can be silently dropped while the original message is
        // acked. Confirms must be enabled to match the main Producer channel.
        var (conn, _, _) = MockConnection();
        var tcfg = MakeTransportCfg();
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object, retry, audit, NullLogger.Instance);

        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        conn.Verify(c => c.CreateChannelAsync(
                It.Is<CreateChannelOptions?>(o =>
                    o != null
                    && o.PublisherConfirmationsEnabled
                    && o.PublisherConfirmationTrackingEnabled),
                It.IsAny<CancellationToken>()),
            Times.Once);
        conn.Verify(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartConsumingAsync_CancellationToken_FlowsToConnectionCreateChannel()
    {
        // Broker/DNS/TCP stalls during channel open must honor the startup CT —
        // a cancelled StartConsumingAsync must not block on connection setup.
        CancellationToken consumerToken = default;
        CancellationToken publishToken = default;
        var consumerChannel = CreateMockChannel();
        var publishChannel = CreateMockChannel();
        var conn = new Mock<IServiceConnectConnection>();
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .Callback<CancellationToken>(ct => consumerToken = ct)
            .ReturnsAsync(consumerChannel.Object);
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<CreateChannelOptions?, CancellationToken>((_, ct) => publishToken = ct)
            .ReturnsAsync(publishChannel.Object);

        var tcfg = MakeTransportCfg();
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object, retry, audit, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q", cancellationToken: cts.Token);

        Assert.Equal(cts.Token, consumerToken);
        Assert.Equal(cts.Token, publishToken);
    }

    [Fact]
    public async Task EventAsync_DoesNotObserveStartupCancellation()
    {
        // Delivery callbacks must observe the consumer-lifetime token, not the
        // startup token. Otherwise a caller cancelling the startup CT after
        // StartConsumingAsync returns would hand every subsequent delivery a
        // pre-cancelled token and the handler would never run.
        var (conn, _, _) = MockConnection();
        var tcfg = MakeTransportCfg();
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        CancellationToken observed = new CancellationToken(canceled: true);
        ConsumerEventHandler handler = (body, type, headers, ct) =>
        {
            observed = ct;
            return Task.FromResult(new ConsumeEventResult { Success = true });
        };

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object, retry, audit, NullLogger.Instance);

        using var startupCts = new CancellationTokenSource();
        await host.StartConsumingAsync(handler, "q", cancellationToken: startupCts.Token);

        // Simulate the startup scope ending — caller cancels its startup CT.
        startupCts.Cancel();

        await DeliverMessageAsync(host, new byte[] { 1 }, new Dictionary<string, object>
        {
            [HeaderKeys.TypeName] = System.Text.Encoding.UTF8.GetBytes(typeof(object).FullName!),
        });

        Assert.False(observed.IsCancellationRequested,
            "Delivery callback must not observe the startup cancellation token.");
    }

    [Fact]
    public async Task ConsumeMessageTypeAsync_BindsQueueToExchange()
    {
        var (conn, channel, _) = MockConnection();
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
        var (conn, channel, _) = MockConnection();
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
        var (conn, channel, _) = MockConnection();
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
        var (conn, channel, _) = MockConnection();
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

    [Fact]
    public async Task DisposeAsync_WithoutInFlightWork_CompletesImmediately()
    {
        var (conn, channel, _) = MockConnection();
        var timeProvider = new FakeTimeProvider();
        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg(prefetch: 10, autoDelete: false, disablePrefetch: false, gracefulShutdownTimeoutMs: 50).Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(MakeQueueCfg().Object),
            NullLogger.Instance,
            timeProvider);

        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        var disposeTask = host.DisposeAsync().AsTask();
        await Task.Yield();

        Assert.True(disposeTask.IsCompleted);
        await disposeTask;

        channel.Verify(c => c.BasicCancelAsync("tag", false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.CloseAsync(200, "Goodbye", false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_CancelsConsumerBeforeClosingChannel()
    {
        var (conn, channel, _) = MockConnection();
        var sequence = new MockSequence();
        channel.InSequence(sequence)
            .Setup(c => c.BasicCancelAsync("tag", false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.InSequence(sequence)
            .Setup(c => c.CloseAsync(200, "Goodbye", false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg().Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(MakeQueueCfg().Object),
            NullLogger.Instance);

        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        await host.DisposeAsync();

        channel.Verify(c => c.BasicCancelAsync("tag", false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.CloseAsync(200, "Goodbye", false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_WaitsForInFlightMessageToCompleteWithinGraceWindow()
    {
        var (conn, channel, _) = MockConnection();
        var timeProvider = new FakeTimeProvider();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowHandlerToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Setup(c => c.BasicCancelAsync("tag", false, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                cancelObserved.SetResult();
                await Task.CompletedTask;
            });
        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg(prefetch: 10, autoDelete: false, disablePrefetch: false, gracefulShutdownTimeoutMs: 500).Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(MakeQueueCfg().Object),
            NullLogger.Instance,
            timeProvider);

        await host.StartConsumingAsync(
            async (_, _, _, _) =>
            {
                handlerStarted.SetResult();
                await allowHandlerToFinish.Task;
                return new ConsumeEventResult { Success = true };
            },
            "q");

        var deliveryTask = DeliverMessageAsync(host, new byte[1], new Dictionary<string, object> { [HeaderKeys.TypeName] = "SomeType" });
        await handlerStarted.Task;

        var disposeTask = host.DisposeAsync().AsTask();
        await cancelObserved.Task;

        Assert.False(disposeTask.IsCompleted);
        timeProvider.Advance(TimeSpan.FromMilliseconds(499));
        await Task.Yield();

        Assert.False(disposeTask.IsCompleted);
        allowHandlerToFinish.SetResult();
        timeProvider.Advance(TimeSpan.FromMilliseconds(50));

        await disposeTask;
        await deliveryTask;

        channel.Verify(c => c.BasicCancelAsync("tag", false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.CloseAsync(200, "Goodbye", false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_ClosesChannelWhenGraceWindowExpires()
    {
        var (conn, channel, _) = MockConnection();
        var timeProvider = new FakeTimeProvider();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowHandlerToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Setup(c => c.BasicCancelAsync("tag", false, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                cancelObserved.SetResult();
                await Task.CompletedTask;
            });
        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg(prefetch: 10, autoDelete: false, disablePrefetch: false, gracefulShutdownTimeoutMs: 50).Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(MakeQueueCfg().Object),
            NullLogger.Instance,
            timeProvider);

        await host.StartConsumingAsync(
            async (_, _, _, _) =>
            {
                handlerStarted.SetResult();
                await allowHandlerToFinish.Task;
                return new ConsumeEventResult { Success = true };
            },
            "q");

        var deliveryTask = DeliverMessageAsync(host, new byte[1], new Dictionary<string, object> { [HeaderKeys.TypeName] = "SomeType" });
        await handlerStarted.Task;

        var disposeTask = host.DisposeAsync().AsTask();
        await cancelObserved.Task;

        Assert.False(disposeTask.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(49));
        await Task.Yield();

        Assert.False(disposeTask.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await Task.Yield();

        Assert.True(disposeTask.IsCompleted);
        channel.Verify(c => c.BasicCancelAsync("tag", false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.CloseAsync(200, "Goodbye", false, It.IsAny<CancellationToken>()), Times.Once);

        allowHandlerToFinish.SetResult();
        await deliveryTask;
    }

    [Fact]
    public async Task DisposeAsync_CancelsConsumerBeforeWaitingForInFlightHandler()
    {
        var (conn, channel, _) = MockConnection();
        var timeProvider = new FakeTimeProvider();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowHandlerToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Setup(c => c.BasicCancelAsync("tag", false, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                cancelObserved.SetResult();
                await Task.CompletedTask;
            });

        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg(prefetch: 10, autoDelete: false, disablePrefetch: false, gracefulShutdownTimeoutMs: 500).Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(MakeQueueCfg().Object),
            NullLogger.Instance,
            timeProvider);

        await host.StartConsumingAsync(
            async (_, _, _, _) =>
            {
                handlerStarted.SetResult();
                await allowHandlerToFinish.Task;
                return new ConsumeEventResult { Success = true };
            },
            "q");

        var deliveryTask = DeliverMessageAsync(host, new byte[1], new Dictionary<string, object> { [HeaderKeys.TypeName] = "SomeType" });
        await handlerStarted.Task;

        var disposeTask = host.DisposeAsync().AsTask();
        await cancelObserved.Task;

        Assert.False(disposeTask.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(500));
        await disposeTask;

        allowHandlerToFinish.SetResult();
        timeProvider.Advance(TimeSpan.FromMilliseconds(50));
        await deliveryTask;

        channel.Verify(c => c.BasicCancelAsync("tag", false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_WhenGraceWindowExpires_LateFailure_DoesNotRetryOrAck()
    {
        var (conn, channel, _) = MockConnection();
        var timeProvider = new FakeTimeProvider();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowHandlerToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        channel.Setup(c => c.CloseAsync(200, "Goodbye", false, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                closeStarted.SetResult();
                await closeGate.Task;
            });

        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg(prefetch: 10, autoDelete: false, disablePrefetch: false, gracefulShutdownTimeoutMs: 100).Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(MakeQueueCfg().Object),
            NullLogger.Instance,
            timeProvider);

        await host.StartConsumingAsync(
            async (_, _, _, _) =>
            {
                handlerStarted.SetResult();
                await allowHandlerToFinish.Task;
                return new ConsumeEventResult
                {
                    Success = false,
                    Exception = new InvalidOperationException("boom")
                };
            },
            "q");

        var deliveryTask = DeliverMessageAsync(host, new byte[1], new Dictionary<string, object> { [HeaderKeys.TypeName] = "SomeType" });
        await handlerStarted.Task;

        var disposeTask = host.DisposeAsync().AsTask();
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        await closeStarted.Task;

        allowHandlerToFinish.SetResult();
        await deliveryTask;

        channel.Verify(c => c.BasicPublishAsync(
            string.Empty, "q.Retries", false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);

        closeGate.SetResult();
        await disposeTask;
    }

    [Fact]
    public async Task DisposeAsync_AfterCancel_LateDispatch_DoesNotStartHandler_AndLeavesMessageUnackedForRedelivery()
    {
        var (conn, channel, _) = MockConnection();
        var timeProvider = new FakeTimeProvider();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        channel.Setup(c => c.BasicCancelAsync("tag", false, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await releaseCancel.Task;
            });
        channel.Setup(c => c.CloseAsync(200, "Goodbye", false, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                closeStarted.SetResult();
                await closeGate.Task;
            });

        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg(prefetch: 10, autoDelete: false, disablePrefetch: false, gracefulShutdownTimeoutMs: 50).Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(MakeQueueCfg().Object),
            NullLogger.Instance,
            timeProvider);

        await host.StartConsumingAsync(
            (_, _, _, _) =>
            {
                handlerStarted.SetResult();
                return Task.FromResult(new ConsumeEventResult { Success = true });
            },
            "q");

        var disposeTask = host.DisposeAsync().AsTask();
        releaseCancel.SetResult();

        timeProvider.Advance(TimeSpan.FromMilliseconds(50));
        await closeStarted.Task;

        var lateDeliveryTask = DeliverMessageAsync(host, new byte[1], new Dictionary<string, object> { [HeaderKeys.TypeName] = "SomeType" });

        var completed = await Task.WhenAny(handlerStarted.Task, lateDeliveryTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.NotSame(handlerStarted.Task, completed);

        await lateDeliveryTask;

        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);

        closeGate.SetResult();
        await disposeTask;
    }

    [Fact]
    public async Task DisposeAsync_StalledBasicCancel_CompletesWithinGraceWindow()
    {
        var (conn, channel, _) = MockConnection();
        var timeProvider = new FakeTimeProvider();
        var cancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        channel.Setup(c => c.BasicCancelAsync("tag", false, It.IsAny<CancellationToken>()))
            .Returns(cancelGate.Task);

        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg(prefetch: 10, autoDelete: false, disablePrefetch: false, gracefulShutdownTimeoutMs: 50).Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(MakeQueueCfg().Object),
            NullLogger.Instance,
            timeProvider);

        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        var disposeTask = host.DisposeAsync().AsTask();

        Assert.False(disposeTask.IsCompleted);
        timeProvider.Advance(TimeSpan.FromMilliseconds(49));
        await Task.Yield();
        Assert.False(disposeTask.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await Task.Yield();
        Assert.True(disposeTask.IsCompleted);

        await disposeTask;

        channel.Verify(c => c.BasicCancelAsync("tag", false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.Dispose(), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_StalledClose_CompletesWithinGraceWindow()
    {
        var (conn, channel, _) = MockConnection();
        var timeProvider = new FakeTimeProvider();
        var closeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        channel.Setup(c => c.CloseAsync(200, "Goodbye", false, It.IsAny<CancellationToken>()))
            .Returns(closeGate.Task);

        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg(prefetch: 10, autoDelete: false, disablePrefetch: false, gracefulShutdownTimeoutMs: 50).Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(MakeQueueCfg().Object),
            NullLogger.Instance,
            timeProvider);

        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        var disposeTask = host.DisposeAsync().AsTask();

        Assert.False(disposeTask.IsCompleted);
        timeProvider.Advance(TimeSpan.FromMilliseconds(49));
        await Task.Yield();
        Assert.False(disposeTask.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await Task.Yield();
        Assert.True(disposeTask.IsCompleted);

        await disposeTask;

        channel.Verify(c => c.CloseAsync(200, "Goodbye", false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.Dispose(), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_HonorsRemainingGraceWindowBelowPollInterval()
    {
        var (conn, channel, _) = MockConnection();
        var timeProvider = new FakeTimeProvider();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowHandlerToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg(prefetch: 10, autoDelete: false, disablePrefetch: false, gracefulShutdownTimeoutMs: 25).Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(MakeQueueCfg().Object),
            NullLogger.Instance,
            timeProvider);

        await host.StartConsumingAsync(
            async (_, _, _, _) =>
            {
                handlerStarted.SetResult();
                await allowHandlerToFinish.Task;
                return new ConsumeEventResult { Success = true };
            },
            "q");

        var deliveryTask = DeliverMessageAsync(host, new byte[1], new Dictionary<string, object> { [HeaderKeys.TypeName] = "SomeType" });
        await handlerStarted.Task;

        var disposeTask = host.DisposeAsync().AsTask();

        timeProvider.Advance(TimeSpan.FromMilliseconds(24));
        await Task.Yield();
        Assert.False(disposeTask.IsCompleted);

        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await Task.Yield();
        Assert.True(disposeTask.IsCompleted);

        allowHandlerToFinish.SetResult();
        await deliveryTask;
        channel.Verify(c => c.CloseAsync(200, "Goodbye", false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_WhenRetryPublishStalls_CancelsPublishAtShutdownDeadline_AndLeavesMessageUnacked()
    {
        var (conn, channel, publishChannel) = MockConnection();
        var timeProvider = new FakeTimeProvider();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Retry publishes ride the dedicated publish channel.
        publishChannel.Setup(c => c.BasicPublishAsync(
                string.Empty,
                "q.Retries",
                false,
                It.IsAny<BasicProperties>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>((_, _, _, _, _, cancellationToken) =>
            {
                publishStarted.TrySetResult();
                cancellationToken.Register(() => publishCanceled.TrySetResult());
                return new ValueTask(Task.Delay(Timeout.Infinite, cancellationToken));
            });

        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg(prefetch: 10, autoDelete: false, disablePrefetch: false, gracefulShutdownTimeoutMs: 100).Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(MakeQueueCfg().Object),
            NullLogger.Instance,
            timeProvider);

        await host.StartConsumingAsync(
            (_, _, _, _) =>
            {
                handlerStarted.SetResult();
                return Task.FromResult(new ConsumeEventResult
                {
                    Success = false,
                    Exception = new InvalidOperationException("boom")
                });
            },
            "q");

        var deliveryTask = DeliverMessageAsync(host, new byte[1], new Dictionary<string, object> { [HeaderKeys.TypeName] = "SomeType" });
        await handlerStarted.Task;
        await publishStarted.Task;

        var disposeTask = host.DisposeAsync().AsTask();

        timeProvider.Advance(TimeSpan.FromMilliseconds(100));

        var canceled = await Task.WhenAny(publishCanceled.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(publishCanceled.Task, canceled);

        var completed = await Task.WhenAny(deliveryTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(deliveryTask, completed);

        await deliveryTask;
        await disposeTask;

        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DisposeAsync_WhenAuditPublishStalls_CancelsPublishAtShutdownDeadline_AndLeavesMessageUnacked()
    {
        var (conn, channel, publishChannel) = MockConnection();
        var timeProvider = new FakeTimeProvider();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var qcfg = MakeQueueCfg();
        qcfg.SetupGet(c => c.AuditingEnabled).Returns(true);

        // Audit publishes ride the dedicated publish channel.
        publishChannel.Setup(c => c.BasicPublishAsync(
                "audit",
                string.Empty,
                false,
                It.IsAny<BasicProperties>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken>((_, _, _, _, _, cancellationToken) =>
            {
                publishStarted.TrySetResult();
                cancellationToken.Register(() => publishCanceled.TrySetResult());
                return new ValueTask(Task.Delay(Timeout.Infinite, cancellationToken));
            });

        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg(prefetch: 10, autoDelete: false, disablePrefetch: false, gracefulShutdownTimeoutMs: 100).Object,
            qcfg.Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(qcfg.Object),
            NullLogger.Instance,
            timeProvider);

        await host.StartConsumingAsync(
            (_, _, _, _) =>
            {
                handlerStarted.SetResult();
                return Task.FromResult(new ConsumeEventResult { Success = true });
            },
            "q");

        var deliveryTask = DeliverMessageAsync(
            host,
            new byte[1],
            new Dictionary<string, object>
            {
                [HeaderKeys.TypeName] = "SomeType",
                [HeaderKeys.MessageType] = "SomeMessage"
            });
        await handlerStarted.Task;
        await publishStarted.Task;

        var disposeTask = host.DisposeAsync().AsTask();

        timeProvider.Advance(TimeSpan.FromMilliseconds(100));

        var canceled = await Task.WhenAny(publishCanceled.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(publishCanceled.Task, canceled);

        var completed = await Task.WhenAny(deliveryTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(deliveryTask, completed);

        await deliveryTask;
        await disposeTask;

        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EventAsync_WhenAuditPublishThrows_MessageIsAcked_AndHandlerNotRedelivered()
    {
        // C4: after the handler succeeds, an audit-publish failure must not fail delivery —
        // audit is observability, not part of the business transaction. The original message
        // must be ack'd and the handler must not run a second time.
        var (conn, channel, publishChannel) = MockConnection();
        var qcfg = MakeQueueCfg();
        qcfg.SetupGet(c => c.AuditingEnabled).Returns(true);

        publishChannel.Setup(c => c.BasicPublishAsync(
                "audit",
                string.Empty,
                false,
                It.IsAny<BasicProperties>(),
                It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated audit publish failure"));

        var handlerInvocations = 0;
        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg().Object,
            qcfg.Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(qcfg.Object),
            NullLogger.Instance);

        await host.StartConsumingAsync(
            (_, _, _, _) =>
            {
                Interlocked.Increment(ref handlerInvocations);
                return Task.FromResult(new ConsumeEventResult { Success = true });
            },
            "q");

        await DeliverMessageAsync(
            host,
            new byte[1],
            new Dictionary<string, object>
            {
                [HeaderKeys.TypeName] = "SomeType",
                [HeaderKeys.MessageType] = "SomeMessage"
            });

        Assert.Equal(1, handlerInvocations);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Inbound message-size enforcement.

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
        var (conn, channel, publishChannel) = MockConnection();
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
        // Error-exchange publishes flow through the dedicated publish channel.
        publishChannel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EventAsync_ExactLimitMessage_IsProcessed()
    {
        const long maxSize = 10L;
        var (conn, channel, _) = MockConnection();
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

    // Inbound header count and value size limits.

    [Fact]
    public async Task EventAsync_ExcessiveHeaderCount_IsNacked_AndHandlerNotInvoked()
    {
        var (conn, channel, publishChannel) = MockConnection();
        var tcfg = MakeTransportCfg();
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        bool handlerInvoked = false;
        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object, retry, audit, NullLogger.Instance);
        await host.StartConsumingAsync(
            (_, _, _, _) => { handlerInvoked = true; return Task.FromResult(new ConsumeEventResult { Success = true }); },
            "q");

        // Build a headers dict with more than DefaultMaxHeaderCount (64) entries.
        var tooManyHeaders = new Dictionary<string, object> { [HeaderKeys.TypeName] = "SomeType" };
        for (int i = 0; i < 65; i++)
            tooManyHeaders[$"X-Excess-{i}"] = "v";

        await DeliverMessageAsync(host, new byte[1], tooManyHeaders);

        Assert.False(handlerInvoked, "Handler must not be invoked when header count exceeds limit.");
        publishChannel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EventAsync_OversizedHeaderValue_IsNacked_AndHandlerNotInvoked()
    {
        var (conn, channel, publishChannel) = MockConnection();
        var tcfg = MakeTransportCfg();
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);

        bool handlerInvoked = false;
        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object, retry, audit, NullLogger.Instance);
        await host.StartConsumingAsync(
            (_, _, _, _) => { handlerInvoked = true; return Task.FromResult(new ConsumeEventResult { Success = true }); },
            "q");

        // Single header with a byte[] value exceeding DefaultMaxHeaderValueBytes (8192).
        var bigValueHeaders = new Dictionary<string, object>
        {
            [HeaderKeys.TypeName] = "SomeType",
            ["X-Big-Value"] = new byte[8193],
        };

        await DeliverMessageAsync(host, new byte[1], bigValueHeaders);

        Assert.False(handlerInvoked, "Handler must not be invoked when a header value exceeds size limit.");
        publishChannel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EventAsync_MissingTypeHeaders_PublishesToErrorExchange_Acks_AndHandlerNotInvoked()
    {
        var (conn, channel, publishChannel) = MockConnection();
        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg().Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(MakeQueueCfg().Object),
            NullLogger.Instance);

        bool handlerInvoked = false;
        await host.StartConsumingAsync(
            (_, _, _, _) =>
            {
                handlerInvoked = true;
                return Task.FromResult(new ConsumeEventResult { Success = true });
            },
            "q");

        await DeliverMessageAsync(host, new byte[] { 1, 2, 3 }, headers: null);

        Assert.False(handlerInvoked);
        publishChannel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EventAsync_InvalidMessage_WhenErrorPublishFails_IsNackedForRedelivery()
    {
        var (conn, channel, publishChannel) = MockConnection();
        // Error publish now rides the publish channel. Make the publish-channel
        // publish fail — consumer-channel publish is still a no-op success stub.
        publishChannel.Setup(c => c.BasicPublishAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broker unavailable"));

        var qcfg = MakeQueueCfg();
        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfgWithMaxSize(10).Object,
            qcfg.Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(qcfg.Object),
            NullLogger.Instance);

        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");
        await DeliverMessageAsync(host, new byte[11], new Dictionary<string, object> { [HeaderKeys.TypeName] = "SomeType" });

        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), false, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EventAsync_NotHandled_WithDeadLetterDisabled_DoesNotRouteToErrorExchange()
    {
        // Default behaviour: a handler-less message is acked as Success so the broker
        // stops redelivering, and no terminal-failure publish is made. Audit still fires
        // when enabled — this test locks in that historical behaviour.
        var (conn, channel, publishChannel) = MockConnection();
        var qcfg = MakeQueueCfg();
        qcfg.SetupGet(c => c.AuditingEnabled).Returns(true);
        var busCfg = MakeBusCfg();
        busCfg.SetupGet(c => c.DeadLetterUnhandledMessages).Returns(false);

        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg().Object,
            qcfg.Object,
            busCfg.Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(qcfg.Object),
            NullLogger.Instance);

        await host.StartConsumingAsync(
            (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true, NotHandled = true }),
            "q");

        await DeliverMessageAsync(host, new byte[1], new Dictionary<string, object> { [HeaderKeys.TypeName] = "SomeType" });

        publishChannel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        // Audit publish still runs — backward-compat invariant.
        publishChannel.Verify(c => c.BasicPublishAsync(
            "audit", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EventAsync_NotHandled_WithDeadLetterEnabled_RoutesToErrorExchange()
    {
        var (conn, channel, publishChannel) = MockConnection();
        var qcfg = MakeQueueCfg();
        var busCfg = MakeBusCfg();
        busCfg.SetupGet(c => c.DeadLetterUnhandledMessages).Returns(true);

        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg().Object,
            qcfg.Object,
            busCfg.Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(qcfg.Object),
            NullLogger.Instance);

        await host.StartConsumingAsync(
            (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true, NotHandled = true }),
            "q");

        await DeliverMessageAsync(host, new byte[1], new Dictionary<string, object> { [HeaderKeys.TypeName] = "SomeType" });

        // Terminal-failure publishes flow through the dedicated publish channel to
        // the error exchange — no retry queue involvement.
        publishChannel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        // The original delivery is acked once its terminal publish is confirmed.
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EventAsync_NotHandled_WithErrorsDisabled_DoesNotRouteToErrorExchange()
    {
        // DisableErrors short-circuits the error exchange regardless of
        // DeadLetterUnhandledMessages — terminal routing must respect it.
        var (conn, channel, publishChannel) = MockConnection();
        var qcfg = MakeQueueCfg();
        qcfg.SetupGet(c => c.DisableErrors).Returns(true);
        var busCfg = MakeBusCfg();
        busCfg.SetupGet(c => c.DeadLetterUnhandledMessages).Returns(true);

        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg().Object,
            qcfg.Object,
            busCfg.Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(qcfg.Object),
            NullLogger.Instance);

        await host.StartConsumingAsync(
            (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true, NotHandled = true }),
            "q");

        await DeliverMessageAsync(host, new byte[1], new Dictionary<string, object> { [HeaderKeys.TypeName] = "SomeType" });

        publishChannel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    // M14 — null-valued TypeName header survives ContainsKey but crashes dispatch

    [Fact]
    public async Task EventAsync_NullValuedTypeNameHeader_RejectsAtAdmission_WithoutBurningRetryBudget()
    {
        // Admission check used ContainsKey which admits a key whose value is null.
        // CopyInboundHeaders skips null values → dispatch-site indexer throws KeyNotFoundException.
        // The message burns a retry cycle instead of being terminated at admission.
        // Fix: admission must check TryGetValue+non-null instead of ContainsKey.
        var (conn, channel, publishChannel) = MockConnection();
        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg().Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(MakeQueueCfg().Object),
            NullLogger.Instance);

        bool handlerInvoked = false;
        await host.StartConsumingAsync(
            (_, _, _, _) =>
            {
                handlerInvoked = true;
                return Task.FromResult(new ConsumeEventResult { Success = true });
            },
            "q");

        // TypeName key is present but value is null — simulates non-.NET client omitting the value.
        await DeliverMessageAsync(host, new byte[] { 1, 2, 3 },
            new Dictionary<string, object> { [HeaderKeys.TypeName] = null! });

        Assert.False(handlerInvoked, "Handler must not be invoked when TypeName value is null.");
        // Must route to error (terminal rejection), NOT to retry queue.
        publishChannel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        publishChannel.Verify(c => c.BasicPublishAsync(
            string.Empty, "q.Retries", false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EventAsync_NullValuedFullTypeNameHeader_AlsoRejectsAtAdmission()
    {
        // Same bug applies to FullTypeName key.
        var (conn, channel, publishChannel) = MockConnection();
        var host = new RabbitMqConsumerHost(
            conn.Object,
            MakeTransportCfg().Object,
            MakeQueueCfg().Object,
            MakeBusCfg().Object,
            new MessageRetryHandler(3, "err", NullLogger.Instance),
            new MessageAuditPublisher(MakeQueueCfg().Object),
            NullLogger.Instance);

        bool handlerInvoked = false;
        await host.StartConsumingAsync(
            (_, _, _, _) =>
            {
                handlerInvoked = true;
                return Task.FromResult(new ConsumeEventResult { Success = true });
            },
            "q");

        await DeliverMessageAsync(host, new byte[] { 1, 2, 3 },
            new Dictionary<string, object> { [HeaderKeys.FullTypeName] = null! });

        Assert.False(handlerInvoked, "Handler must not be invoked when FullTypeName value is null.");
        publishChannel.Verify(c => c.BasicPublishAsync(
            "err", string.Empty, false,
            It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    // M13 — broker-initiated shutdown event subscriptions

    [Fact]
    public async Task StartConsumingAsync_ConsumerShutdownAsync_IsSubscribed_AndLogsWarning()
    {
        // After the fix: ShutdownAsync on the consumer must be subscribed.
        // Fire the consumer's HandleChannelShutdownAsync (which raises ShutdownAsync)
        // and assert the host logs a Warning containing "shutdown".
        var (conn, _, _) = MockConnection();
        var tcfg = MakeTransportCfg();
        var qcfg = MakeQueueCfg();
        var logMessages = new System.Collections.Concurrent.ConcurrentBag<(Microsoft.Extensions.Logging.LogLevel Level, string Message)>();
        var testLogger = new CapturingLogger(logMessages);
        var retry = new MessageRetryHandler(3, "err", testLogger);
        var audit = new MessageAuditPublisher(qcfg.Object);

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object, retry, audit, testLogger);
        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        // Retrieve the private _consumer via reflection and fire HandleChannelShutdownAsync.
        var consumerField = typeof(RabbitMqConsumerHost)
            .GetField("_consumer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var consumer = consumerField?.GetValue(host) as RabbitMQ.Client.Events.AsyncEventingBasicConsumer;
        Assert.NotNull(consumer);

        var shutdownArgs = new RabbitMQ.Client.Events.ShutdownEventArgs(
            RabbitMQ.Client.ShutdownInitiator.Peer, 320, "Queue deleted by broker");
        await consumer.HandleChannelShutdownAsync(consumer, shutdownArgs);

        // The handler must have logged a Warning containing "shutdown".
        var shutdownLog = logMessages.FirstOrDefault(m =>
            m.Level >= Microsoft.Extensions.Logging.LogLevel.Warning
            && m.Message.Contains("shutdown", StringComparison.OrdinalIgnoreCase));

        Assert.False(shutdownLog == default,
            "Expected a Warning-level log mentioning 'shutdown' after consumer ShutdownAsync fired. " +
            "This confirms M13: ShutdownAsync event is not subscribed.");
    }

    [Fact]
    public async Task StartConsumingAsync_ConsumerUnregisteredAsync_IsSubscribed_AndLogsWarning()
    {
        // After the fix: UnregisteredAsync on the consumer must be subscribed.
        // This event fires on broker-initiated basic.cancel (e.g. queue deleted while consuming).
        // Fire it via HandleBasicCancelAsync and verify the Warning is logged.
        var (conn, _, _) = MockConnection();
        var tcfg = MakeTransportCfg();
        var qcfg = MakeQueueCfg();
        var shutdownObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logMessages = new System.Collections.Concurrent.ConcurrentBag<(Microsoft.Extensions.Logging.LogLevel Level, string Message)>();
        var testLogger = new CapturingLogger(logMessages, (level, message) =>
        {
            if (level >= Microsoft.Extensions.Logging.LogLevel.Warning
                && message.Contains("shutdown", StringComparison.OrdinalIgnoreCase))
                shutdownObserved.TrySetResult(true);
        });
        var retry = new MessageRetryHandler(3, "err", testLogger);
        var audit = new MessageAuditPublisher(qcfg.Object);

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object, retry, audit, testLogger);
        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        var consumerField = typeof(RabbitMqConsumerHost)
            .GetField("_consumer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var consumer = consumerField?.GetValue(host) as RabbitMQ.Client.Events.AsyncEventingBasicConsumer;
        Assert.NotNull(consumer);

        // HandleBasicCancelAsync triggers UnregisteredAsync (broker-initiated cancel).
        await consumer.HandleBasicCancelAsync("tag");

        await shutdownObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var unregisteredLog = logMessages.FirstOrDefault(m =>
            m.Level >= Microsoft.Extensions.Logging.LogLevel.Warning
            && m.Message.Contains("shutdown", StringComparison.OrdinalIgnoreCase));

        Assert.False(unregisteredLog == default,
            "Expected a Warning-level log mentioning 'shutdown' after consumer UnregisteredAsync fired. " +
            "This confirms M13: UnregisteredAsync event is not subscribed.");
    }

    [Fact]
    public async Task StartConsumingAsync_ChannelShutdownAsync_IsSubscribed_AndLogsWarning()
    {
        // IChannel.ChannelShutdownAsync must also be subscribed.
        // We set up the channel mock to raise the event and verify the logger received a warning.
        var (conn, consumerChannel, _) = MockConnection();
        var tcfg = MakeTransportCfg();
        var qcfg = MakeQueueCfg();
        var shutdownObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logMessages = new System.Collections.Concurrent.ConcurrentBag<(Microsoft.Extensions.Logging.LogLevel Level, string Message)>();
        var testLogger = new CapturingLogger(logMessages, (level, message) =>
        {
            if (level >= Microsoft.Extensions.Logging.LogLevel.Warning
                && message.Contains("shutdown", StringComparison.OrdinalIgnoreCase))
                shutdownObserved.TrySetResult(true);
        });
        var retry = new MessageRetryHandler(3, "err", testLogger);
        var audit = new MessageAuditPublisher(qcfg.Object);

        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object, retry, audit, testLogger);
        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        // Raise the ChannelShutdownAsync event on the consumer channel mock.
        var shutdownArgs = new RabbitMQ.Client.Events.ShutdownEventArgs(
            RabbitMQ.Client.ShutdownInitiator.Peer, 320, "Channel closed by broker");
        consumerChannel.Raise(c => c.ChannelShutdownAsync += null, consumerChannel.Object, shutdownArgs);

        await shutdownObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var channelShutdownLog = logMessages.FirstOrDefault(m =>
            m.Level >= Microsoft.Extensions.Logging.LogLevel.Warning
            && m.Message.Contains("shutdown", StringComparison.OrdinalIgnoreCase));

        Assert.False(channelShutdownLog == default,
            "Expected a Warning-level log mentioning 'shutdown' after ChannelShutdownAsync fired. " +
            "This confirms M13: ChannelShutdownAsync event is not subscribed.");
    }

    [Fact]
    public async Task StartConsumingAsync_DisposesPreviouslyAssignedCtsFields()
    {
        // L6: Field-initialised CTS instances and any CTS from a previous start must be
        // disposed before being replaced in StartConsumingAsync — otherwise restart leaks.
        var (conn, _, _) = MockConnection();
        var tcfg = MakeTransportCfg();
        var qcfg = MakeQueueCfg();
        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher(qcfg.Object);
        var host = new RabbitMqConsumerHost(conn.Object, tcfg.Object, qcfg.Object, MakeBusCfg().Object, retry, audit, NullLogger.Instance);

        var deliveryCtsField = typeof(RabbitMqConsumerHost)
            .GetField("_deliveryCts", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var shutdownCtsField = typeof(RabbitMqConsumerHost)
            .GetField("_shutdownPublishCts", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var initialDeliveryCts = (CancellationTokenSource)deliveryCtsField.GetValue(host)!;
        var initialShutdownCts = (CancellationTokenSource)shutdownCtsField.GetValue(host)!;

        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "test-queue");

        Assert.Throws<ObjectDisposedException>(() => initialDeliveryCts.Cancel());
        Assert.Throws<ObjectDisposedException>(() => initialShutdownCts.Cancel());

        await host.DisposeAsync();
    }

    /// <summary>
    /// Minimal ILogger that captures log messages for assertion.
    /// Optionally accepts an <paramref name="onLog"/> callback invoked after each log entry
    /// (useful for TCS-based synchronisation without introducing Task.Delay races).
    /// </summary>
    private sealed class CapturingLogger(
        System.Collections.Concurrent.ConcurrentBag<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> bag,
        Action<Microsoft.Extensions.Logging.LogLevel, string>? onLog = null) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            bag.Add((logLevel, message));
            onLog?.Invoke(logLevel, message);
        }
    }
}
