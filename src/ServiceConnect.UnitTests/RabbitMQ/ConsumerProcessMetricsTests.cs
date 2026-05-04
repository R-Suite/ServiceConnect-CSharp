using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.UnitTests.Diagnostics;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Drives <see cref="RabbitMqConsumerHost"/> through its three observable per-delivery outcomes
/// (success, error, retry) and asserts the OTel-standard process metrics fire with the
/// correct <c>messaging.outcome</c> tag.
/// </summary>
public sealed class ConsumerProcessMetricsTests
{
    [Fact]
    public async Task ProcessAsync_HandlerSucceeds_RecordsDurationAndConsumedSuccess()
    {
        // Per-test unique queue name lets MetricCollector's destination-name filter isolate
        // emissions from other tests running in parallel that also drive the consumer host.
        var queueName = $"q-success-{Guid.NewGuid():N}";
        using var collector = new MetricCollector("messaging.destination.name", queueName);

        // success path: handler returns Success=true; ProcessAsync runs to completion, returns true,
        // no exception. Outcome = "success".
        static Task<ConsumeEventResult> Handler(
            ReadOnlyMemory<byte> _, string __, IDictionary<string, object> ___, CancellationToken ____)
            => Task.FromResult(new ConsumeEventResult { Success = true });

        var (host, _, _) = await BuildHostAsync(Handler, queueName: queueName);
        await using (host)
        {
            await host.RaiseDeliveryForTests(MakeArgs(deliveryTag: 1));
        }

        var duration = Assert.Single(collector.GetDoubleRecords(MetricNames.ProcessDuration));
        Assert.Equal("rabbitmq", duration.GetTag("messaging.system"));
        Assert.Equal("process", duration.GetTag("messaging.operation"));
        Assert.Equal(queueName, duration.GetTag("messaging.destination.name"));
        Assert.Null(duration.GetTag("error.type"));
        Assert.True(duration.Value >= 0);

        var consumed = Assert.Single(collector.GetLongRecords(MetricNames.ConsumedMessages));
        Assert.Equal(1, consumed.Value);
        Assert.Equal(queueName, consumed.GetTag("messaging.destination.name"));
        Assert.Equal("success", consumed.GetTag("messaging.outcome"));
        Assert.Null(consumed.GetTag("error.type"));
    }

    [Fact]
    public async Task ProcessAsync_HandlerSucceedsButRetryPublishThrows_RecordsConsumedError()
    {
        var queueName = $"q-error-{Guid.NewGuid():N}";
        using var collector = new MetricCollector("messaging.destination.name", queueName);

        // error path: handler returns Success=false; the retry-publish throws AlreadyClosedException
        // which InboundMessageProcessor explicitly rethrows. ProcessAsync's exception escapes to
        // ProcessWithMetricsAsync → metric outcome = "error".
        static Task<ConsumeEventResult> Handler(
            ReadOnlyMemory<byte> _, string __, IDictionary<string, object> ___, CancellationToken ____)
            => Task.FromResult(new ConsumeEventResult { Success = false, Exception = new InvalidOperationException("handler failed") });

        var (host, _, _) = await BuildHostAsync(Handler, queueName: queueName, retryPublishThrows: true);
        await using (host)
        {
            await host.RaiseDeliveryForTests(MakeArgs(deliveryTag: 2));
        }

        var durationRecords = collector.GetDoubleRecords(MetricNames.ProcessDuration);
        var duration = Assert.Single(durationRecords);
        Assert.Equal("rabbitmq", duration.GetTag("messaging.system"));
        Assert.Equal("process", duration.GetTag("messaging.operation"));
        Assert.Equal(queueName, duration.GetTag("messaging.destination.name"));
        // error.type populated from ExceptionTypeMapper for the rethrown AlreadyClosedException.
        Assert.NotNull(duration.GetTag("error.type"));

        var consumedRecords = collector.GetLongRecords(MetricNames.ConsumedMessages);
        var consumed = Assert.Single(consumedRecords);
        Assert.Equal(1, consumed.Value);
        Assert.Equal("error", consumed.GetTag("messaging.outcome"));
        Assert.NotNull(consumed.GetTag("error.type"));
    }

    [Fact]
    public async Task ProcessAsync_ShutdownTimedOut_RecordsConsumedRetry()
    {
        var queueName = $"q-retry-{Guid.NewGuid():N}";
        using var collector = new MetricCollector("messaging.destination.name", queueName);

        // retry path: handler returns Success=false; the host's shutdown-timed-out flag is set
        // before processing starts, so ProcessAsync's early-return on the failure branch returns
        // false (processed=false, no exception). Outcome = "retry".
        static Task<ConsumeEventResult> Handler(
            ReadOnlyMemory<byte> _, string __, IDictionary<string, object> ___, CancellationToken ____)
            => Task.FromResult(new ConsumeEventResult { Success = false, Exception = new InvalidOperationException("handler failed") });

        var (host, _, _) = await BuildHostAsync(Handler, queueName: queueName);

        // Force the shutdown-timed-out flag to true so the ProcessAsync failure branch returns false
        // BEFORE attempting the retry publish — that's the documented "retry"/redelivery condition.
        var field = typeof(RabbitMqConsumerHost).GetField(
            "_shutdownTimedOut",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field!.SetValue(host, 1);

        await using (host)
        {
            await host.RaiseDeliveryForTests(MakeArgs(deliveryTag: 3));
        }

        var duration = Assert.Single(collector.GetDoubleRecords(MetricNames.ProcessDuration));
        Assert.Equal(queueName, duration.GetTag("messaging.destination.name"));
        Assert.Null(duration.GetTag("error.type"));

        var consumed = Assert.Single(collector.GetLongRecords(MetricNames.ConsumedMessages));
        Assert.Equal(1, consumed.Value);
        Assert.Equal("retry", consumed.GetTag("messaging.outcome"));
        Assert.Null(consumed.GetTag("error.type"));
    }

    [Fact]
    public async Task OnMessageReceived_TogglesInFlightUpDownCounter()
    {
        // Drives a successful dispatch through RabbitMqConsumerHost.EventAsync so the
        // admission-site +1 and the finally-site -1 both fire. Net delta must be zero —
        // an unmatched +1 would surface as a steadily-climbing in-flight gauge.
        var queueName = $"q-inflight-{Guid.NewGuid():N}";
        using var collector = new MetricCollector("messaging.destination.name", queueName);

        static Task<ConsumeEventResult> Handler(
            ReadOnlyMemory<byte> _, string __, IDictionary<string, object> ___, CancellationToken ____)
            => Task.FromResult(new ConsumeEventResult { Success = true });

        var (host, _, _) = await BuildHostAsync(Handler, queueName: queueName);
        await using (host)
        {
            await host.RaiseDeliveryForTests(MakeArgs(deliveryTag: 4));
        }

        var deltas = collector.GetLongRecords(MetricNames.InFlightMessages);
        Assert.Equal(2, deltas.Count);
        Assert.Equal(1, deltas[0].Value);   // +1 at admission
        Assert.Equal(-1, deltas[1].Value);  // -1 in the finally
        Assert.Equal(0, deltas.Sum(r => r.Value));  // net-zero invariant
        Assert.All(deltas, r =>
        {
            Assert.Equal("rabbitmq", r.GetTag("messaging.system"));
            Assert.Equal(queueName, r.GetTag("messaging.destination.name"));
        });
    }

    // ── Harness ───────────────────────────────────────────────────────────────
    // Mirrors RabbitMqConsumerHostInflightCounterTests.BuildHostAsync; parameterised
    // to optionally raise AlreadyClosedException from the publish channel so the
    // retry-publish error rethrow is exercised.
    private static async Task<(
        RabbitMqConsumerHost Host,
        Mock<IChannel> ConsumerChannel,
        Mock<IChannel> PublishChannel)> BuildHostAsync(
        ConsumerEventHandler handler,
        string queueName,
        bool retryPublishThrows = false)
    {
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
        consumerChannel.Setup(c => c.BasicCancelAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumerChannel.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumerChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        consumerChannel.SetupAdd(c => c.ChannelShutdownAsync += It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());
        consumerChannel.SetupRemove(c => c.ChannelShutdownAsync -= It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());

        var publishChannel = new Mock<IChannel>(MockBehavior.Loose);
        if (retryPublishThrows)
        {
            // AlreadyClosedException is on InboundMessageProcessor's rethrow list — it'll escape
            // ProcessAsync and surface as outcome=error in the metric scope.
            publishChannel
                .Setup(c => c.BasicPublishAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                    It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new global::RabbitMQ.Client.Exceptions.AlreadyClosedException(
                    new ShutdownEventArgs(ShutdownInitiator.Application, 0, "test")));
        }
        else
        {
            publishChannel
                .Setup(c => c.BasicPublishAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                    It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                    It.IsAny<CancellationToken>()))
                .Returns(ValueTask.CompletedTask);
        }
        publishChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var conn = new Mock<IServiceConnectConnection>();
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(consumerChannel.Object);
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(publishChannel.Object);
        conn.SetupGet(c => c.UnderlyingConnection).Returns((IConnection?)null);

        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.MaxRetries).Returns(3);
        transport.SetupGet(t => t.PrefetchCount).Returns((ushort)10);
        transport.SetupProperty(t => t.GracefulShutdownTimeoutMilliseconds, 5000);
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns(queueName);
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

        await host.StartConsumingAsync(handler, queueName).ConfigureAwait(false);

        return (host, consumerChannel, publishChannel);
    }

    private static BasicDeliverEventArgs MakeArgs(ulong deliveryTag)
        => new(
            consumerTag: "ct",
            deliveryTag: deliveryTag,
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
}
