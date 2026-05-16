using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies that <see cref="ConnectionFactoryBuilder"/> disables library-level topology recovery
/// while keeping connection auto-recovery enabled. Application-level recovery is the canonical
/// path: <c>Consumer.StartConsumingAsync</c> redeclares exchanges, queues, and bindings on every
/// connect, and <c>ProducerConnection.EnsureExchangeDeclaredAsync</c> uses a generation-keyed
/// declare cache. Library topology recovery would duplicate that work and silently fail on
/// topology drift (PRECONDITION_FAILED), so it is deliberately off.
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
    public void Build_TopologyRecoveryEnabled_IsFalse()
    {
        var factory = ConnectionFactoryBuilder.Build(MinimalTransport());

        Assert.False(factory.TopologyRecoveryEnabled);
    }

    [Fact]
    public void Build_AutomaticRecoveryEnabled_IsTrue()
    {
        // Connection-level recovery is still desired; only topology recovery is off.
        var factory = ConnectionFactoryBuilder.Build(MinimalTransport());

        Assert.True(factory.AutomaticRecoveryEnabled);
    }
}
