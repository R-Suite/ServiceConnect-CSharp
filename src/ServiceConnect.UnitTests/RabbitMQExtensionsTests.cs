using Microsoft.Extensions.DependencyInjection;
using ServiceConnect;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests;

public class RabbitMQExtensionsTests
{
    [Fact]
    public void UseRabbitMQ_RegistersProducerAndConsumer()
    {
        var builder = new ServiceConnectBuilder();

        builder.UseRabbitMQ();

        // Verify registration action was added and resolves IProducer/IConsumer
        Assert.Single(builder.AdditionalRegistrations);
        var services = new ServiceCollection();
        builder.AdditionalRegistrations[0](services);

        Assert.Contains(services, sd => sd.ServiceType == typeof(IProducer));
        Assert.Contains(services, sd => sd.ServiceType == typeof(IConsumer));
    }

    [Fact]
    public void UseRabbitMQ_AppliesTransportConfig()
    {
        var builder = new ServiceConnectBuilder();

        builder.UseRabbitMQ(t => t.Host = "myhost");

        Assert.Equal("myhost", builder.BusConfig.Transport.Host);
    }
}
