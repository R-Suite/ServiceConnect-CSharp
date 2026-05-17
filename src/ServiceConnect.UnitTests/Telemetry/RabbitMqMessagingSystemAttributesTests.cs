using Moq;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Telemetry;
using Xunit;

namespace ServiceConnect.UnitTests.Telemetry;

public sealed class RabbitMqMessagingSystemAttributesTests
{
    [Theory]
    [InlineData("rabbit1", "rabbit1")]
    [InlineData("rabbit1,rabbit2", "rabbit1")]
    [InlineData("rabbit1;rabbit2", "rabbit1;rabbit2")]  // transport treats semicolon as part of hostname
    [InlineData(" rabbit1 , rabbit2 ", "rabbit1")]
    [InlineData("", "")]
    public void ServerAddress_HandlesClusterListSeparators(string host, string expected)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns(host);
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());
        var attrs = new RabbitMqMessagingSystemAttributes(transport.Object);
        Assert.Equal(expected, attrs.ServerAddress);
    }
}
