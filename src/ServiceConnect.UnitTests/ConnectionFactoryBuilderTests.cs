using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

public class ConnectionFactoryBuilderTests
{
    private static Mock<ITransportConfiguration> Transport(IReadOnlyDictionary<string, object>? clientSettings = null)
    {
        var mock = new Mock<ITransportConfiguration>();
        mock.SetupGet(t => t.Host).Returns("localhost");
        mock.SetupGet(t => t.ClientSettings).Returns(clientSettings ?? new Dictionary<string, object>());
        return mock;
    }

    [Fact]
    public void Build_WhenHeartbeatTimeConfigured_HonoursConfiguredInterval()
    {
        // Heartbeat resolution lives in ConnectionFactoryBuilder so producer and
        // consumer both honour the configured HeartbeatTime. The built factory must
        // carry the caller's value rather than falling back to the RabbitMQ default.
        var transport = Transport(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.HeartbeatTime] = 45,
        });

        var factory = ConnectionFactoryBuilder.Build(transport.Object);

        Assert.Equal(TimeSpan.FromSeconds(45), factory.RequestedHeartbeat);
    }

    [Fact]
    public void Build_WhenHeartbeatDisabled_UsesZero()
    {
        // TimeSpan.Zero tells the RabbitMQ client to suppress heartbeats entirely.
        var transport = Transport(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.HeartbeatEnabled] = false,
            [RabbitMQSettingKeys.HeartbeatTime] = 45,
        });

        var factory = ConnectionFactoryBuilder.Build(transport.Object);

        Assert.Equal(TimeSpan.Zero, factory.RequestedHeartbeat);
    }

    [Fact]
    public void Build_WhenNoHeartbeatConfigured_Uses120SecondDefault()
    {
        var factory = ConnectionFactoryBuilder.Build(Transport().Object);

        Assert.Equal(TimeSpan.FromSeconds(120), factory.RequestedHeartbeat);
    }
}
