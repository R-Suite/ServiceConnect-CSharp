using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

public class ProducerLifecycleTests
{
    private static Producer CreateProducer()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        return new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);
    }

    private static void SetField<T>(Producer producer, string fieldName, T value) =>
        ProducerInternals.SetField(producer, fieldName, value);

    [Fact]
    public async Task DisposeAsync_WhenCalledConcurrently_DoesNotThrowAndClosesResourcesOnce()
    {
        var producer = CreateProducer();
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(true);
        connection.Setup(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        SetField(producer, "_model", channel.Object);
        SetField(producer, "_connection", connection.Object);
        SetField(producer, "_connected", true);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => producer.DisposeAsync().AsTask());

        var ex = await Record.ExceptionAsync(() => Task.WhenAll(tasks));

        Assert.Null(ex);
        channel.Verify(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.Dispose(), Times.Once);
        connection.Verify(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(c => c.Dispose(), Times.Once);
    }

    [Fact]
    public void Producer_DoesNotExposeDisconnectAsync()
    {
        // The legacy DisconnectAsync was removed in v7. Lifecycle is now exclusively
        // owned by IAsyncDisposable.DisposeAsync — pinning this here so a future refactor
        // doesn't accidentally re-introduce the dual-API hazard (where some callers used
        // DisconnectAsync without DisposeAsync and leaked the underlying connection).
        var method = typeof(Producer).GetMethod("DisconnectAsync");
        Assert.Null(method);
    }
}
