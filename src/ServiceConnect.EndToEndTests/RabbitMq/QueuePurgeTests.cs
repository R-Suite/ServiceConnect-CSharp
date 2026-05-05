using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.DependencyInjection;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Helpers;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class QueuePurgeTests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task PurgeQueueOnStartup_ExistingMessages_ClearedBeforeConsuming()
    {
        // Arrange: pre-populate queue with messages using raw RabbitMQ client
        var queueName = _fixture.GetUniqueQueueName("purge");
        var tcs = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var factory = new ConnectionFactory
        {
            HostName = _fixture.RabbitMqHostname,
            Port = _fixture.RabbitMqPort,
            UserName = _fixture.RabbitMqUsername,
            Password = _fixture.RabbitMqPassword
        };

        // Create queue and publish stale messages directly
        {
            await using var conn = await factory.CreateConnectionAsync();
            await using var channel = await conn.CreateChannelAsync();

            await channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false);

            // Bind to the TestMessage exchange so ServiceConnect can find it
            var exchangeName = typeof(TestMessage).FullName!;
            await channel.ExchangeDeclareAsync(exchangeName, "fanout", durable: true);
            await channel.QueueBindAsync(queueName, exchangeName, string.Empty);

            // Publish 3 stale messages
            var props = new BasicProperties
            {
                Headers = new Dictionary<string, object?>
                {
                    ["MessageType"] = Encoding.UTF8.GetBytes(typeof(TestMessage).FullName!)
                }
            };
            for (int i = 0; i < 3; i++)
            {
                await channel.BasicPublishAsync("", queueName, mandatory: false, props, Encoding.UTF8.GetBytes($"{{\"Content\":\"stale-{i}\"}}"));
            }
        }

        // Now start ServiceConnect bus with PurgeQueueOnStartup=true
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
            new CallbackHandler<TestMessage>(msg => tcs.TrySetResult(msg)));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3);
                t.SetClientSetting("RetrySeconds", 1);
                t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
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

        await using var purgeConn = await factory.CreateConnectionAsync();
        await using var purgeChannel = await purgeConn.CreateChannelAsync();
        var purgeResult = await TestPolling.WaitUntilAsync(
            async () =>
            {
                var get = await purgeChannel.BasicGetAsync(queueName, autoAck: true);
                return get is null;
            },
            TimeSpan.FromSeconds(5));
        Assert.True(purgeResult, "Queue did not become empty within timeout.");

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
            await bus.DisposeAsync();
            if (provider is IAsyncDisposable asyncProvider)
            {
                await asyncProvider.DisposeAsync();
            }
        }
    }
}
