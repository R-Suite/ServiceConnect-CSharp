using Microsoft.Extensions.DependencyInjection;
using ServiceConnect;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Client.RabbitMQ.Configuration;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

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

    [Fact]
    public void UseRabbitMQ_WithTypedOptions_StuffsClientSettings()
    {
        var builder = new ServiceConnectBuilder();

        builder.UseRabbitMQ((RabbitMqOptions opts) =>
        {
            opts.PrefetchCount = 25;
            opts.HeartbeatTime = 30;
            opts.PublisherAcknowledgements = true;
        });

        var transport = builder.BusConfig.Transport;
        Assert.True(transport.ClientSettings.TryGetValue(RabbitMQSettingKeys.PrefetchCount, out var prefetch));
        Assert.Equal((ushort)25, prefetch);
        Assert.True(transport.ClientSettings.TryGetValue(RabbitMQSettingKeys.HeartbeatTime, out var heartbeat));
        Assert.Equal((ushort)30, heartbeat);
        Assert.True(transport.ClientSettings.TryGetValue(RabbitMQSettingKeys.PublisherAcknowledgements, out var acks));
        Assert.Equal(true, acks);
    }

    [Fact]
    public void UseRabbitMQ_WithTypedOptions_NullPropertyLeavesClientSettingsUntouched()
    {
        var builder = new ServiceConnectBuilder();
        // Pre-seed a value via the stringly-typed API before calling the typed overload.
        builder.ConfigureTransport(t => t.SetClientSetting(RabbitMQSettingKeys.PrefetchCount, (ushort)50));

        builder.UseRabbitMQ((RabbitMqOptions opts) =>
        {
            opts.HeartbeatTime = 30;  // unrelated property — PrefetchCount stays null → must not overwrite
        });

        Assert.True(builder.BusConfig.Transport.ClientSettings.TryGetValue(RabbitMQSettingKeys.PrefetchCount, out var prefetch));
        Assert.Equal((ushort)50, prefetch);  // preserved
    }

    [Fact]
    public void UseRabbitMQ_WithTypedOptions_NullLambda_RegistersProducerAndConsumer()
    {
        var builder = new ServiceConnectBuilder();

        builder.UseRabbitMQ((Action<RabbitMqOptions>?)null);

        Assert.Single(builder.AdditionalRegistrations);
        var services = new ServiceCollection();
        builder.AdditionalRegistrations[0](services);
        Assert.Contains(services, sd => sd.ServiceType == typeof(IProducer));
        Assert.Contains(services, sd => sd.ServiceType == typeof(IConsumer));
    }
}
