using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies that <see cref="Producer"/> does not short-circuit reconnect when
/// <c>_connected</c> is true but the underlying <c>_model</c> channel is closed.
/// A broker drop closes the channel without clearing <c>_connected</c>; the
/// fast-path early-exit must check channel liveness, not only the flag.
/// </summary>
public class ProducerEnsureConnectedTests
{
    private static Producer CreateProducer()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");

        var settings = new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.RetryCount] = (ushort)1,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)0,
        };
        transport.SetupGet(t => t.ClientSettings).Returns(settings);

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        return new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);
    }

    private static void SetField<T>(Producer producer, string fieldName, T value) =>
        ProducerInternals.SetField(producer, fieldName, value);

    [Fact]
    public async Task EnsureConnectedAsync_WhenConnectedFlagTrueButChannelClosed_AttemptsReconnect()
    {
        // Arrange: producer that believes it is connected but whose channel has been closed
        // (simulates a broker drop after initial connect).
        await using var producer = CreateProducer();

        var closedChannel = new Mock<IChannel>();
        closedChannel.SetupGet(c => c.IsOpen).Returns(false);

        SetField(producer, "_connected", true);
        SetField(producer, "_model", closedChannel.Object);

        int reconnectCallCount = 0;

        var healthyChannel = new Mock<IChannel>();
        healthyChannel.SetupGet(c => c.IsOpen).Returns(true);

        var freshConnection = new Mock<IConnection>();
        freshConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(healthyChannel.Object);
        freshConnection.SetupGet(c => c.IsOpen).Returns(true);

        producer.CreateConnectionForTests = (_, _, _, _) =>
        {
            Interlocked.Increment(ref reconnectCallCount);
            return Task.FromResult(freshConnection.Object);
        };

        // Act: invoke the private EnsureConnectedAsync(CancellationToken) overload directly.
        var method = typeof(Producer).GetMethod(
            "EnsureConnectedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(CancellationToken)],
            modifiers: null)!;

        await (Task)method.Invoke(producer, [CancellationToken.None])!;

        // Assert: the connection factory hook was called, proving reconnect was attempted
        // rather than skipping through the early-exit on the stale _connected flag.
        Assert.Equal(1, reconnectCallCount);
    }
}
