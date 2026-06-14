using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies the <see cref="RabbitMQSettingKeys.NetworkRecoveryInterval"/> client-setting wires
/// through to <see cref="ConnectionFactory.NetworkRecoveryInterval"/>. The unset path keeps
/// RabbitMQ.Client's own default; non-TimeSpan values throw with the key, value, and type so
/// misconfiguration is loud.
/// </summary>
public sealed class ConnectionFactoryBuilderRecoveryIntervalTests
{
    private static ITransportConfiguration TransportWithSettings(IDictionary<string, object> settings)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>(settings));
        return transport.Object;
    }

    [Fact]
    public void Build_WithNetworkRecoveryIntervalSet_AppliesToFactory()
    {
        var transport = TransportWithSettings(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.NetworkRecoveryInterval] = TimeSpan.FromSeconds(30),
        });

        var factory = ConnectionFactoryBuilder.Build(transport);

        Assert.Equal(TimeSpan.FromSeconds(30), factory.NetworkRecoveryInterval);
    }

    [Fact]
    public void Build_WithoutNetworkRecoveryInterval_KeepsRabbitMqClientDefault()
    {
        var transport = TransportWithSettings(new Dictionary<string, object>());

        var factory = ConnectionFactoryBuilder.Build(transport);

        // Don't assert a specific value here — that would couple this test to RabbitMQ.Client's
        // current 5-second default. Instead, compare against a freshly-constructed
        // ConnectionFactory: whatever the upstream default is, ours must match it when the
        // setting is unset.
        var defaultFactory = new ConnectionFactory();
        Assert.Equal(defaultFactory.NetworkRecoveryInterval, factory.NetworkRecoveryInterval);
    }

    [Fact]
    public void Build_WithNetworkRecoveryIntervalNonTimeSpan_Throws()
    {
        var transport = TransportWithSettings(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.NetworkRecoveryInterval] = "30s",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => ConnectionFactoryBuilder.Build(transport));

        Assert.Contains(RabbitMQSettingKeys.NetworkRecoveryInterval, ex.Message);
        Assert.Contains("30s", ex.Message);
        Assert.Contains("System.String", ex.Message);
    }
}
