using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Helpers;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class MalformedMessageTests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

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
                MessageType = typeof(TestMessage)
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
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3);
                t.SetClientSetting("RetrySeconds", 1);
                t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
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

            {
                await using var conn = await factory.CreateConnectionAsync();
                await using var channel = await conn.CreateChannelAsync();

                var props = new BasicProperties
                {
                    Headers = new Dictionary<string, object?>
                    {
                        ["FullTypeName"] = Encoding.UTF8.GetBytes(typeof(TestMessage).AssemblyQualifiedName!),
                        ["MessageType"] = Encoding.UTF8.GetBytes(typeof(TestMessage).FullName!),
                        ["MessageId"] = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString())
                    }
                };
                await channel.BasicPublishAsync("", queueName, mandatory: false, props, Encoding.UTF8.GetBytes("{{{INVALID JSON}}}"));
            }

            // Assert: poll the error queue for the dead-lettered message
            var pollFactory = new ConnectionFactory
            {
                HostName = _fixture.RabbitMqHostname,
                Port = _fixture.RabbitMqPort,
                UserName = _fixture.RabbitMqUsername,
                Password = _fixture.RabbitMqPassword
            };

            await using var pollConn = await pollFactory.CreateConnectionAsync();
            await using var pollChannel = await pollConn.CreateChannelAsync();

            var errorMsg = await TestPolling.WaitForAsync(
                async () => await pollChannel.BasicGetAsync(errorQueueName, autoAck: true),
                timeout: TimeSpan.FromSeconds(30));

            Assert.NotNull(errorMsg);
        }
        finally
        {
            await bus.DisposeAsync();
            if (provider is IAsyncDisposable asyncProvider)
            {
                await asyncProvider.DisposeAsync();
            }
        }
    }
}
