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

        Assert.NotEmpty(builder.AdditionalRegistrations);
    }

    [Fact]
    public void UseRabbitMQ_AppliesTransportConfig()
    {
        var builder = new ServiceConnectBuilder();

        builder.UseRabbitMQ(t => t.Host = "myhost");

        Assert.Equal("myhost", builder.BusConfig.Transport.Host);
    }
}
