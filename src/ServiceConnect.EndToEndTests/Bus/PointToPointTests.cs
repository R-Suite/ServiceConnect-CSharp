using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.DependencyInjection;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(IsolatedCollection))]
public class PointToPointTests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

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
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3);
                t.SetClientSetting("RetrySeconds", 1);
                t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(c => c.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IBus>();
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task SendAsync_MessageIsPublishedToRabbitMQ()
    {
        var queueName = _fixture.GetUniqueQueueName("send");

        // Producer publishes with mandatory:true so a Send to an undeclared queue surfaces
        // NO_ROUTE rather than silently dropping at the broker. The producer-only setup
        // below does not start a consumer (no UseRabbitMQ binding), so declare the
        // destination queue directly via RabbitMQ.Client before the send.
        var preDeclareFactory = new global::RabbitMQ.Client.ConnectionFactory
        {
            HostName = _fixture.RabbitMqHostname,
            Port = _fixture.RabbitMqPort,
            UserName = _fixture.RabbitMqUsername,
            Password = _fixture.RabbitMqPassword,
        };
        await using (var preConn = await preDeclareFactory.CreateConnectionAsync())
        await using (var preCh = await preConn.CreateChannelAsync())
        {
            await preCh.QueueDeclareAsync(queueName, durable: false, exclusive: false, autoDelete: true);
        }

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
