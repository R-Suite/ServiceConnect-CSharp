using System.Reflection;
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
/// Verifies that <see cref="RabbitMqConsumerHost"/> exposes the broker-cancel signal
/// via its internal <c>IsCancelledByBroker</c> getter. The host's
/// <c>OnConsumerUnregisteredAsync</c> handler must flip the flag synchronously so a
/// downstream health probe racing with the broker-cancel event sees Unhealthy on the
/// same tick the operator first sees the warning log.
/// </summary>
public sealed class RabbitMqConsumerHostBrokerCancelTests
{
    [Fact]
    public async Task NewHost_IsCancelledByBroker_IsFalse()
    {
        var (host, _, _) = await BuildHostAsync();

        Assert.False(host.IsCancelledByBroker);
    }

    [Fact]
    public async Task OnConsumerUnregistered_SetsIsCancelledByBroker()
    {
        var (host, _, _) = await BuildHostAsync();

        // OnConsumerUnregisteredAsync is private — invoke via reflection. The signature is
        // (object? sender, ConsumerEventArgs args) and ConsumerEventArgs requires a non-null
        // string[] of consumer tags.
        var method = typeof(RabbitMqConsumerHost).GetMethod(
            "OnConsumerUnregisteredAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var args = new ConsumerEventArgs(["tag"]);
        var result = (Task)method!.Invoke(host, [null, args])!;
        await result;

        Assert.True(host.IsCancelledByBroker);
    }

    // ── Harness (mirror of RabbitMqConsumerHostInflightCounterTests.BuildHostAsync) ───

    private static async Task<(
        RabbitMqConsumerHost Host,
        Mock<IChannel> ConsumerChannel,
        Mock<IChannel> PublishChannel)> BuildHostAsync()
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
        consumerChannel.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        consumerChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        consumerChannel.SetupAdd(c => c.ChannelShutdownAsync += It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());
        consumerChannel.SetupRemove(c => c.ChannelShutdownAsync -= It.IsAny<AsyncEventHandler<ShutdownEventArgs>>());

        var publishChannel = new Mock<IChannel>(MockBehavior.Loose);
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

        static async Task<ConsumeEventResult> NoOpHandler(
            ReadOnlyMemory<byte> _, string __, IDictionary<string, object> ___, CancellationToken ____)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            return new ConsumeEventResult { Success = true };
        }

        var host = new RabbitMqConsumerHost(
            conn.Object, transport.Object, queue.Object, bus.Object,
            retry, new RabbitMqAdmissionGate("q"), audit, NullLogger.Instance);

        await host.StartConsumingAsync(NoOpHandler, queueName: "q").ConfigureAwait(false);

        return (host, consumerChannel, publishChannel);
    }
}
