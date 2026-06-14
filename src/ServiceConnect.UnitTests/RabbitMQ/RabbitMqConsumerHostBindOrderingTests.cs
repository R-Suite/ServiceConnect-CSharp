using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies that Consumer.StartConsumingAsync issues QueueBindAsync before BasicConsumeAsync.
/// RabbitMQ.Client requires per-channel serialisation; binding after BasicConsume violates
/// that contract because an in-flight delivery callback can interleave with the bind.
/// </summary>
public sealed class RabbitMqConsumerHostBindOrderingTests
{
    [Fact]
    public async Task ConsumerStartConsumingAsync_BindsBeforeBasicConsume()
    {
        int callOrder = 0;
        int? bindOrder = null;
        int? consumeOrder = null;

        var consumerChannel = new Mock<IChannel>(MockBehavior.Loose);
        consumerChannel.SetupGet(c => c.IsOpen).Returns(true);
        consumerChannel
            .Setup(c => c.QueueBindAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>>(), false, It.IsAny<CancellationToken>()))
            .Callback(() => bindOrder ??= Interlocked.Increment(ref callOrder))
            .Returns(Task.CompletedTask);
        consumerChannel
            .Setup(c => c.BasicConsumeAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<IAsyncBasicConsumer>(), It.IsAny<CancellationToken>()))
            .Callback(() => consumeOrder ??= Interlocked.Increment(ref callOrder))
            .ReturnsAsync("consumer-tag");
        consumerChannel
            .Setup(c => c.BasicQosAsync(It.IsAny<uint>(), It.IsAny<ushort>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Setup channel: used for topology provisioning (QueueDeclareAsync, ExchangeDeclareAsync, etc.)
        var setupChannel = new Mock<IChannel>(MockBehavior.Loose);
        setupChannel.SetupGet(c => c.IsOpen).Returns(true);

        var publishChannel = new Mock<IChannel>(MockBehavior.Loose);
        publishChannel.SetupGet(c => c.IsOpen).Returns(true);

        // The connection is called:
        //   1st CreateChannelAsync(ct)          → setup channel (topology provisioning)
        //   2nd CreateChannelAsync(ct)           → consumer channel (RabbitMqConsumerHost)
        //   3rd CreateChannelAsync(options, ct)  → publish channel (RabbitMqConsumerHost)
        int createChannelCallIndex = 0;
        var connection = new Mock<IServiceConnectConnection>();
        connection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                int idx = Interlocked.Increment(ref createChannelCallIndex);
                return idx == 1 ? setupChannel.Object : consumerChannel.Object;
            });
        connection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(publishChannel.Object);
        connection.SetupGet(c => c.UnderlyingConnection).Returns((IConnection?)null);

        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());
        transport.SetupGet(t => t.PrefetchCount).Returns((ushort)1);
        transport.SetupGet(t => t.GracefulShutdownTimeoutMilliseconds).Returns(1000);
        transport.SetupGet(t => t.MaxRetries).Returns(3);
        transport.SetupGet(t => t.RetryDelay).Returns(0);

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("main-q");
        queueConfig.SetupGet(q => q.ErrorQueueName).Returns("error.exchange");
        queueConfig.SetupGet(q => q.AuditingEnabled).Returns(false);
        queueConfig.SetupGet(q => q.PurgeQueueOnStartup).Returns(false);
        queueConfig.SetupGet(q => q.DisableErrors).Returns(false);

        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.ConsumerCount).Returns(1);

        var consumer = new Consumer(transport.Object, queueConfig.Object, busConfig.Object, NullLogger<Consumer>.Instance, connection.Object);

        static Task<ConsumeEventResult> Handler(ReadOnlyMemory<byte> body, string type, IDictionary<string, object> headers, CancellationToken ct)
            => Task.FromResult(new ConsumeEventResult { Success = true });

        await consumer.StartConsumingAsync("main-q", ["MyApp.Foo"], Handler);

        Assert.NotNull(bindOrder);
        Assert.NotNull(consumeOrder);
        Assert.True(bindOrder < consumeOrder, $"Expected QueueBindAsync ({bindOrder}) before BasicConsumeAsync ({consumeOrder})");

        await consumer.DisposeAsync();
    }
}
