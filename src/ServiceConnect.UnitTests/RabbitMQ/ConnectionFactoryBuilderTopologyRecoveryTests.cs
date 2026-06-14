using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies that <see cref="ConnectionFactoryBuilder"/> enables both connection auto-recovery
/// and library-level topology recovery. Topology recovery is required for HA cluster failover:
/// the application only redeclares topology during startup and has no hook on
/// <c>IConnection.RecoverySucceededAsync</c>, so a recovered connection to a fresh broker node
/// must rely on the library to redeclare exchanges, queues, and bindings. The library's recovery
/// is idempotent for ServiceConnect's declarations (durable, no passive calls, fixed arguments).
/// </summary>
public sealed class ConnectionFactoryBuilderTopologyRecoveryTests
{
    private static ITransportConfiguration MinimalTransport()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());
        return transport.Object;
    }

    [Fact]
    public void Build_TopologyRecoveryEnabled_IsTrue()
    {
        var factory = ConnectionFactoryBuilder.Build(MinimalTransport());

        Assert.True(factory.TopologyRecoveryEnabled);
    }

    [Fact]
    public void Build_AutomaticRecoveryEnabled_IsTrue()
    {
        var factory = ConnectionFactoryBuilder.Build(MinimalTransport());

        Assert.True(factory.AutomaticRecoveryEnabled);
    }
}
