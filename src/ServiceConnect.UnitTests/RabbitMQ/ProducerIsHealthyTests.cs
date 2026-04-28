using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.UnitTests;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies that <see cref="Producer.IsHealthy"/> reflects the underlying
/// <c>ProducerConnection.IsHealthy()</c> state — the public surface that
/// <c>ProducerConnectionHealthCheck</c> observes.
/// </summary>
public class ProducerIsHealthyTests
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

    [Fact]
    public void IsHealthy_FreshProducer_ReturnsFalse()
    {
        var producer = CreateProducer();
        Assert.False(producer.IsHealthy);
    }

    [Fact]
    public void IsHealthy_ConnectedAndChannelOpen_ReturnsTrue()
    {
        var producer = CreateProducer();
        var openChannel = new Mock<IChannel>();
        openChannel.SetupGet(c => c.IsOpen).Returns(true);

        ProducerInternals.SetField(producer, "_connected", true);
        ProducerInternals.SetField(producer, "_model", openChannel.Object);

        Assert.True(producer.IsHealthy);
    }

    [Fact]
    public void IsHealthy_ConnectedButChannelClosed_ReturnsFalse()
    {
        var producer = CreateProducer();
        var closedChannel = new Mock<IChannel>();
        closedChannel.SetupGet(c => c.IsOpen).Returns(false);

        ProducerInternals.SetField(producer, "_connected", true);
        ProducerInternals.SetField(producer, "_model", closedChannel.Object);

        Assert.False(producer.IsHealthy);
    }
}
