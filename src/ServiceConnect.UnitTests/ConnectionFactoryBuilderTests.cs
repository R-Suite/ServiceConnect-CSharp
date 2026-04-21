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
        // H20 regression: Producer previously passed null heartbeat and got RabbitMQ
        // defaults regardless of configuration. Heartbeat resolution now lives in the
        // builder so every caller honours the same settings.
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
