using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class PointToPointTests
{
    private readonly MessagingFixture _fixture;

    public PointToPointTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    private IBus CreateBus(string queueName)
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
        return provider.GetRequiredService<IBus>();
    }

    [Fact]
    public async Task SendAsync_MessageIsPublishedToRabbitMQ()
    {
        var queueName = _fixture.GetUniqueQueueName("send");
        var bus = CreateBus(queueName);

        var message = new TestMessage(Guid.NewGuid()) { Content = "point-to-point test" };

        var exception = await Record.ExceptionAsync(() =>
            bus.SendAsync(message, new SendOptions { EndPoint = queueName }));

        Assert.Null(exception);
    }

    [Fact]
    public async Task PublishAsync_MessageIsPublishedToExchange()
    {
        var queueName = _fixture.GetUniqueQueueName("publish");
        var bus = CreateBus(queueName);

        var message = new TestMessage(Guid.NewGuid()) { Content = "publish test" };

        var exception = await Record.ExceptionAsync(() => bus.PublishAsync(message));

        Assert.Null(exception);
    }
}
