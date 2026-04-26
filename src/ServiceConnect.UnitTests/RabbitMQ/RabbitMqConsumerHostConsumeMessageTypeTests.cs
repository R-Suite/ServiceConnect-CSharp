using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

public class RabbitMqConsumerHostConsumeMessageTypeTests
{
    private static Mock<IChannel> CreateMockChannel()
    {
        var channel = new Mock<IChannel>(MockBehavior.Strict);
        channel.Setup(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.BasicQosAsync(It.IsAny<uint>(), It.IsAny<ushort>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.Setup(c => c.BasicConsumeAsync(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(),
            It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<IDictionary<string, object?>?>(),
            It.IsAny<IAsyncBasicConsumer>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("tag");
        channel.Setup(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return channel;
    }

    private static (RabbitMqConsumerHost Host, Mock<IChannel> Channel) CreateHostWithChannel()
    {
        var consumerChannel = CreateMockChannel();
        var publishChannel = new Mock<IChannel>();
        publishChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var conn = new Mock<IServiceConnectConnection>();
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>())).ReturnsAsync(consumerChannel.Object);
        conn.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(publishChannel.Object);

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

        var retry = new MessageRetryHandler(3, "err", NullLogger.Instance);
        var audit = new MessageAuditPublisher(queue.Object);

        var host = new RabbitMqConsumerHost(conn.Object, transport.Object, queue.Object, bus.Object, retry, audit, NullLogger.Instance);
        return (host, consumerChannel);
    }

    [Fact]
    public async Task ConsumeMessageTypeAsync_PreCancelledToken_ThrowsAndDoesNotBind()
    {
        var (host, channel) = CreateHostWithChannel();

        await host.StartConsumingAsync((_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }), "q");

        // QueueBindAsync must NEVER be called when the token is pre-cancelled.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.ConsumeMessageTypeAsync("MyMessageType", cts.Token));

        channel.Verify(
            c => c.QueueBindAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
