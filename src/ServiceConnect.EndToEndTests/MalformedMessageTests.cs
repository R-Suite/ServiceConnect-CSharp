using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class MalformedMessageTests
{
    private readonly MessagingFixture _fixture;

    public MalformedMessageTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task MalformedJson_MessageSentToErrorQueue()
    {
        // Arrange
        var queueName = _fixture.GetUniqueQueueName("malformed");
        var errorQueueName = _fixture.GetUniqueQueueName("malformed-eq");

        var handlerReferences = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(CallbackHandler<TestMessage>),
                MessageType = typeof(TestMessage),
                RoutingKeys = new List<string>()
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerReferences);
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(_ => { /* should not be reached */ }));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.MaxRetries = 0; // fast error queue delivery — no retries
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q =>
            {
                q.QueueName = queueName;
                q.ErrorQueueName = errorQueueName;
            });
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            // Act: publish corrupt JSON directly via raw RabbitMQ client (bypassing bus serialization)
            var factory = new ConnectionFactory
            {
                HostName = _fixture.RabbitMqHostname,
                Port = _fixture.RabbitMqPort,
                UserName = _fixture.RabbitMqUsername,
                Password = _fixture.RabbitMqPassword
            };

            using (var conn = factory.CreateConnection())
            using (var channel = conn.CreateModel())
            {
                var props = channel.CreateBasicProperties();
                props.Headers = new Dictionary<string, object>
                {
                    ["FullTypeName"] = Encoding.UTF8.GetBytes(typeof(TestMessage).AssemblyQualifiedName!),
                    ["MessageType"] = Encoding.UTF8.GetBytes(typeof(TestMessage).FullName!),
                    ["MessageId"] = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString())
                };
                channel.BasicPublish("", queueName, props, Encoding.UTF8.GetBytes("{{{INVALID JSON}}}"));
            }

            // Assert: poll the error queue for the dead-lettered message
            var pollFactory = new ConnectionFactory
            {
                HostName = _fixture.RabbitMqHostname,
                Port = _fixture.RabbitMqPort,
                UserName = _fixture.RabbitMqUsername,
                Password = _fixture.RabbitMqPassword
            };

            using var pollConn = pollFactory.CreateConnection();
            using var pollChannel = pollConn.CreateModel();

            BasicGetResult? errorMsg = null;
            for (int i = 0; i < 30 && errorMsg == null; i++)
            {
                errorMsg = pollChannel.BasicGet(errorQueueName, autoAck: true);
                if (errorMsg == null) await Task.Delay(1000);
            }

            Assert.NotNull(errorMsg);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}
