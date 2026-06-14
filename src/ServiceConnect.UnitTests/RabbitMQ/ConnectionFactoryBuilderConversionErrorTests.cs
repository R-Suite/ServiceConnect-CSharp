using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class ConnectionFactoryBuilderConversionErrorTests
{
    private static ITransportConfiguration TransportWithSettings(IDictionary<string, object> settings)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>(settings));
        return transport.Object;
    }

    [Fact]
    public void Build_PortIsUnconvertibleString_ThrowsInvalidOperationWithKeyAndValueAndType()
    {
        var transport = TransportWithSettings(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.Port] = "not-a-port",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => ConnectionFactoryBuilder.Build(transport));

        Assert.Contains(RabbitMQSettingKeys.Port, ex.Message);
        Assert.Contains("not-a-port", ex.Message);
        Assert.Contains("System.String", ex.Message);
        Assert.NotNull(ex.InnerException);
        Assert.IsType<FormatException>(ex.InnerException);
    }

    [Fact]
    public void Build_HeartbeatIsUnconvertibleString_ThrowsInvalidOperationWithKeyAndValueAndType()
    {
        var transport = TransportWithSettings(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.HeartbeatTime] = "abc",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => ConnectionFactoryBuilder.Build(transport));

        Assert.Contains(RabbitMQSettingKeys.HeartbeatTime, ex.Message);
        Assert.Contains("abc", ex.Message);
        Assert.Contains("System.String", ex.Message);
        Assert.NotNull(ex.InnerException);
        Assert.IsType<FormatException>(ex.InnerException);
    }

    [Fact]
    public void Build_PortIsOverflowingLong_ThrowsInvalidOperationWithKey()
    {
        var transport = TransportWithSettings(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.Port] = long.MaxValue,
        });

        var ex = Assert.Throws<InvalidOperationException>(() => ConnectionFactoryBuilder.Build(transport));

        Assert.Contains(RabbitMQSettingKeys.Port, ex.Message);
        Assert.NotNull(ex.InnerException);
        Assert.IsType<OverflowException>(ex.InnerException);
    }

    [Fact]
    public void Build_PortIsValidInt_DoesNotThrow()
    {
        var transport = TransportWithSettings(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.Port] = 5672,
        });

        var factory = ConnectionFactoryBuilder.Build(transport);
        Assert.Equal(5672, factory.Port);
    }
}
