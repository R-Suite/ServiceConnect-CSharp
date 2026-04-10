using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class BusLifecycleTests
{
    private readonly MessagingFixture _fixture;

    public BusLifecycleTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    private (IBus bus, IServiceProvider provider) CreateBus(string queueName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProducer, Producer>();
        services.AddServiceConnect(builder =>
        {
            builder.ConfigureTransport(t =>
            {
                t.Host = $"{_fixture.RabbitMqHostname}:{_fixture.RabbitMqPort}";
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
        });

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IBus>(), provider);
    }

    [Fact]
    public void Bus_StartsAndStopsConsuming()
    {
        var (bus, _) = CreateBus(_fixture.GetUniqueQueueName("lifecycle"));

        Assert.False(bus.IsConnected);

        bus.StartConsuming();
        Assert.True(bus.IsConnected);

        bus.StopConsuming();
        Assert.False(bus.IsConnected);
    }

    [Fact]
    public void Bus_DisposesCleanly()
    {
        var (bus, _) = CreateBus(_fixture.GetUniqueQueueName("lifecycle"));

        bus.StartConsuming();
        Assert.True(bus.IsConnected);

        bus.Dispose();
        Assert.False(bus.IsConnected);
    }

    [Fact]
    public void Bus_DoubleDispose_DoesNotThrow()
    {
        var (bus, _) = CreateBus(_fixture.GetUniqueQueueName("lifecycle"));

        bus.StartConsuming();

        var exception = Record.Exception(() =>
        {
            bus.Dispose();
            bus.Dispose();
        });

        Assert.Null(exception);
    }
}
