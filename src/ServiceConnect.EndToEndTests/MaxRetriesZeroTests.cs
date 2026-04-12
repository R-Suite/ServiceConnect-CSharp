using RabbitMQ.Client;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class MaxRetriesZeroTests
{
    private readonly MessagingFixture _fixture;

    public MaxRetriesZeroTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task MaxRetriesZero_HandlerFails_SentDirectlyToErrorQueue()
    {
        // Arrange
        var queueName = _fixture.GetUniqueQueueName("maxretries-zero");
        var errorQueueName = _fixture.GetUniqueQueueName("maxretries-zero-eq");
        int attemptCount = 0;

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
            new CallbackHandler<TestMessage>(_ =>
            {
                Interlocked.Increment(ref attemptCount);
                throw new InvalidOperationException("Simulated handler failure");
            }));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.MaxRetries = 0;
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
        

        try
        {
            // Act
            var correlationId = Guid.NewGuid();
            var sent = new TestMessage(correlationId) { Content = "zero-retries-fail" };
            await bus.PublishAsync(sent);

            // Assert: poll error queue — message should arrive immediately (no retry delay)
            var factory = new ConnectionFactory
            {
                HostName = _fixture.RabbitMqHostname,
                Port = _fixture.RabbitMqPort,
                UserName = _fixture.RabbitMqUsername,
                Password = _fixture.RabbitMqPassword
            };
            await using var conn = await factory.CreateConnectionAsync();
            await using var channel = await conn.CreateChannelAsync();

            BasicGetResult? errorMsg = null;
            for (int i = 0; i < 30 && errorMsg == null; i++)
            {
                errorMsg = await channel.BasicGetAsync(errorQueueName, autoAck: true);
                if (errorMsg == null) await Task.Delay(500);
            }

            Assert.NotNull(errorMsg);

            // Handler should have been called exactly once — no retries
            Assert.Equal(1, attemptCount);
        }
        finally
        {
            await bus.DisposeAsync();
            if (provider is IAsyncDisposable asyncProvider) await asyncProvider.DisposeAsync();
        }
    }
}
