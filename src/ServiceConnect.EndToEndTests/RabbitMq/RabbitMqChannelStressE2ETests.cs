using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Helpers;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

/// <summary>
/// End-to-end guard that bursts of concurrent poison messages stay within the
/// channel's concurrency discipline. Publishing 20 poison messages at once must not
/// surface CHANNEL_ERROR or AlreadyClosedException, and every message must land in
/// the error queue.
/// </summary>
[Collection(nameof(MessagingCollection))]
public class RabbitMqChannelStressE2ETests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task BurstOfPoisonMessages_NoChannelErrors_AllLandInErrorQueue()
    {
        const int messageCount = 20;

        var queueName = _fixture.GetUniqueQueueName("channel-stress");
        var errorQueueName = _fixture.GetUniqueQueueName("channel-stress-eq");
        var auditQueueName = _fixture.GetUniqueQueueName("channel-stress-aq");

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
            new CallbackHandler<TestMessage>(_ =>
                throw new InvalidOperationException("poison")));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.MaxRetries = 1;
                t.RetryDelay = 500;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3);
                t.SetClientSetting("RetrySeconds", 1);
                t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
            });
            builder.ConfigureQueues(q =>
            {
                q.QueueName = queueName;
                q.ErrorQueueName = errorQueueName;
                q.AuditingEnabled = true;
                q.AuditQueueName = auditQueueName;
            });
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();

        try
        {
            // Publish 20 poison messages in parallel
            var publishTasks = Enumerable.Range(0, messageCount).Select(i =>
                bus.PublishAsync(new TestMessage(Guid.NewGuid()) { Content = $"poison-{i}" }));
            await Task.WhenAll(publishTasks);

            // Assert: all 20 messages eventually land in the error queue
            var factory = new ConnectionFactory
            {
                HostName = _fixture.RabbitMqHostname,
                Port = _fixture.RabbitMqPort,
                UserName = _fixture.RabbitMqUsername,
                Password = _fixture.RabbitMqPassword
            };
            await using var conn = await factory.CreateConnectionAsync();
            await using var channel = await conn.CreateChannelAsync();

            var errorCount = 0;
            var reached = await TestPolling.WaitUntilAsync(async () =>
            {
                var msg = await channel.BasicGetAsync(errorQueueName, autoAck: true);
                if (msg != null)
                {
                    errorCount++;
                }

                return errorCount >= messageCount;
            }, TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(200));

            Assert.True(reached, $"Expected {messageCount} messages in error queue but only got {errorCount}.");
        }
        finally
        {
            await bus.DisposeAsync();
            if (provider is IAsyncDisposable ap)
            {
                await ap.DisposeAsync();
            }
        }
    }
}
