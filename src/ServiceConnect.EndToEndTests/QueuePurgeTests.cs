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
public class QueuePurgeTests
{
    private readonly MessagingFixture _fixture;

    public QueuePurgeTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task PurgeQueueOnStartup_ExistingMessages_ClearedBeforeConsuming()
    {
        // Arrange: pre-populate queue with messages using raw RabbitMQ client
        var queueName = _fixture.GetUniqueQueueName("purge");
        var tcs = new TaskCompletionSource<TestMessage>();

        var factory = new ConnectionFactory
        {
            HostName = _fixture.RabbitMqHostname,
            Port = _fixture.RabbitMqPort,
            UserName = _fixture.RabbitMqUsername,
            Password = _fixture.RabbitMqPassword
        };

        // Create queue and publish stale messages directly
        using (var conn = factory.CreateConnection())
        using (var channel = conn.CreateModel())
        {
            channel.QueueDeclare(queueName, durable: true, exclusive: false, autoDelete: false);

            // Bind to the TestMessage exchange so ServiceConnect can find it
            var exchangeName = typeof(TestMessage).FullName!;
            channel.ExchangeDeclare(exchangeName, "fanout", durable: true);
            channel.QueueBind(queueName, exchangeName, string.Empty);

            // Publish 3 stale messages
            var props = channel.CreateBasicProperties();
            props.Headers = new Dictionary<string, object>
            {
                ["MessageType"] = Encoding.UTF8.GetBytes(typeof(TestMessage).FullName!)
            };
            for (int i = 0; i < 3; i++)
            {
                channel.BasicPublish("", queueName, props, Encoding.UTF8.GetBytes($"{{\"Content\":\"stale-{i}\"}}"));
            }
        }

        // Now start ServiceConnect bus with PurgeQueueOnStartup=true
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
            new CallbackHandler<TestMessage>(msg => tcs.TrySetResult(msg)));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
            });
            builder.ConfigureQueues(q =>
            {
                q.QueueName = queueName;
                q.PurgeQueueOnStartup = true;
            });
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        await Task.Delay(1000); // wait for purge and consumer setup

        try
        {
            // Act: send a fresh message after purge
            var correlationId = Guid.NewGuid();
            var sent = new TestMessage(correlationId) { Content = "fresh-after-purge" };
            await bus.PublishAsync(sent);

            // Assert: only the fresh message is received, not stale ones
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());
            var received = await tcs.Task;

            Assert.Equal("fresh-after-purge", received.Content);
            Assert.Equal(correlationId, received.CorrelationId);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}
