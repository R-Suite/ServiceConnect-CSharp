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
/// Verifies the atomicity discipline of <c>_messagesBeingProcessed</c>: under
/// concurrent deliveries, the inflight counter must never go negative and must
/// settle to zero once all in-flight handlers complete.
///
/// The pre-fix host mixed lock-protected `++` (admission gate) with lock-free
/// <c>Interlocked.Decrement</c> (finally) and <c>Volatile.Read</c> (drain loop).
/// Concurrent decrement during the lock-protected read-modify-write `++` could
/// lose updates, leaving the counter stuck above the true in-flight count.
/// Post-fix, all reads/writes use atomic primitives, so the counter is exact
/// regardless of interleaving.
/// </summary>
public sealed class RabbitMqConsumerHostInflightCounterTests
{
    [Fact]
    public async Task EventAsync_ConcurrentDeliveriesAndDrains_CounterReachesZero_NeverNegative()
    {
        // Yielding handler maximises interleaving by forcing the continuation onto
        // the thread pool — this widens the window between the increment (admission)
        // and decrement (finally) so concurrent producers/decrementers exercise the
        // full read-modify-write race surface that the H3 fix targets.
        static async Task<ConsumeEventResult> YieldingHandler(
            ReadOnlyMemory<byte> _, string __, IDictionary<string, object> ___, CancellationToken ____)
        {
            await Task.Yield();
            return new ConsumeEventResult { Success = true };
        }

        var (host, _, _) = await BuildHostAsync(YieldingHandler);

        // Background sampler reads the counter every 1ms and tracks the minimum value
        // observed. A negative value indicates a lost-update race (decrement applied
        // against a stale read inside the lock-protected `++`).
        int minObserved = int.MaxValue;
        using var samplerCts = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!samplerCts.IsCancellationRequested)
            {
                var v = ReadInflightCount(host);
                int snapshot;
                do { snapshot = minObserved; }
                while (v < snapshot && Interlocked.CompareExchange(ref minObserved, v, snapshot) != snapshot);
                try
                {
                    await Task.Delay(1, samplerCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });

        // 8 concurrent producers, 100 deliveries each, distinct deliveryTags so the
        // mock channel paths don't collide on duplicate ack tags.
        var tasks = Enumerable.Range(0, 8).Select(producerIdx => Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                var args = MakeArgs(deliveryTag: (ulong)((producerIdx * 100) + i));
                await host.RaiseDeliveryForTests(args);
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        await samplerCts.CancelAsync();
        try { await sampler; } catch (OperationCanceledException) { }

        // Counter never went negative.
        Assert.True(minObserved >= 0, $"Counter went negative: min observed = {minObserved}");

        // Brief settle window: the decrement runs in the finally after the handler
        // continuation, which may still be hopping thread-pool slots when WhenAll
        // returns from the producer task (the producer task awaits RaiseDeliveryForTests
        // to completion of EventAsync's finally, but the sampler tracks min, not max).
        await Task.Delay(100);

        // Counter is at zero after drain.
        Assert.Equal(0, ReadInflightCount(host));
    }

    private static int ReadInflightCount(RabbitMqConsumerHost host)
    {
        var field = typeof(RabbitMqConsumerHost).GetField(
            "_messagesBeingProcessed",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return (int)field!.GetValue(host)!;
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="RabbitMqConsumerHost"/> with mocked channels. Copied from
    /// RabbitMqConsumerHostHeaderSizeTests (Task 6) and parameterised to accept the
    /// handler delegate so the inflight-counter test can pass a yielding handler that
    /// maximises interleaving.
    /// </summary>
    private static async Task<(
        RabbitMqConsumerHost Host,
        Mock<IChannel> ConsumerChannel,
        Mock<IChannel> PublishChannel)> BuildHostAsync(ConsumerEventHandler handler)
    {
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
        consumerChannel.SetupAdd(c => c.ChannelShutdownAsync += It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());
        consumerChannel.SetupRemove(c => c.ChannelShutdownAsync -= It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());

        // ── Publish channel (BasicPublishAsync) ─────────────────────────────
        var publishChannel = new Mock<IChannel>(MockBehavior.Loose);
        publishChannel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        publishChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        // ── Connection ──────────────────────────────────────────────────────
        var conn = new Mock<IServiceConnectConnection>();
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(consumerChannel.Object);
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(publishChannel.Object);
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

        var retry = new MessageRetryHandler(3, "err", "q", NullLogger.Instance);
        var audit = new MessageAuditPublisher(queue.Object);

        var host = new RabbitMqConsumerHost(
            conn.Object, transport.Object, queue.Object, bus.Object,
            retry, audit, NullLogger.Instance);

        await host.StartConsumingAsync(handler, queueName: "q").ConfigureAwait(false);

        return (host, consumerChannel, publishChannel);
    }

    /// <summary>
    /// Builds a delivery with the minimal headers that get past the type-name
    /// admission guard so the processor path is reached.
    /// </summary>
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
