using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(IsolatedCollection))]
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
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
        });

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IBus>();
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SendAsync_MessageIsPublishedToRabbitMQ()
    {
        var queueName = _fixture.GetUniqueQueueName("send");
        var bus = CreateBus(queueName);
        try
        {
            var message = new TestMessage(Guid.NewGuid()) { Content = "point-to-point test" };

            var exception = await Record.ExceptionAsync(() =>
                bus.SendAsync(message, new SendOptions { EndPoint = queueName }));

            Assert.Null(exception);
        }
        finally
        {
            await bus.DisposeAsync();
        }
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task PublishAsync_MessageIsPublishedToExchange()
    {
        var queueName = _fixture.GetUniqueQueueName("publish");
        var bus = CreateBus(queueName);
        try
        {
            var message = new TestMessage(Guid.NewGuid()) { Content = "publish test" };

            var exception = await Record.ExceptionAsync(() => bus.PublishAsync(message));

            Assert.Null(exception);
        }
        finally
        {
            await bus.DisposeAsync();
        }
    }
}
