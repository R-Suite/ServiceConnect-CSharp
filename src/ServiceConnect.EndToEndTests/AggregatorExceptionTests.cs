using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class AggregatorExceptionTests
{
    private readonly MessagingFixture _fixture;

    public AggregatorExceptionTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Aggregator_ExecuteThrows_MessageSentToErrorQueue()
    {
        // Arrange
        var queueName = _fixture.GetUniqueQueueName("agg-exception");
        var errorQueueName = _fixture.GetUniqueQueueName("agg-exception-eq");
        const int maxRetries = 1;
        const int retryDelay = 1000;

        var handlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(ThrowingAggregator),
                MessageType = typeof(TestMessage),
                RoutingKeys = new List<string>()
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddTransient<Aggregator<TestMessage>, ThrowingAggregator>();

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.MaxRetries = maxRetries;
                t.RetryDelay = retryDelay;
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
            builder.UseInMemoryPersistence();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        

        try
        {
            // Act: send one message — BatchSize=1 triggers Execute immediately
            var msg = new TestMessage(Guid.NewGuid()) { Content = "agg-exception-trigger" };
            await bus.PublishAsync(msg);

            // Poll error queue: wait long enough for retries + buffer
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
                if (errorMsg == null) await Task.Delay(1000);
            }

            // Assert: message landed in error queue
            Assert.NotNull(errorMsg);

            // Verify Exception header contains the thrown message
            var headers = errorMsg.BasicProperties.Headers!;
            Assert.True(headers.ContainsKey("Exception"));
            var exceptionJson = Encoding.UTF8.GetString((byte[])headers["Exception"]!);
            Assert.Contains("Aggregator Execute failed", exceptionJson);
        }
        finally
        {
            await bus.DisposeAsync();
            (provider as IDisposable)?.Dispose();
        }
    }
}

file class ThrowingAggregator : Aggregator<TestMessage>
{
    public override int BatchSize() => 1;
    public override TimeSpan Timeout() => TimeSpan.FromSeconds(30);
    public override void Execute(IList<TestMessage> messages) =>
        throw new InvalidOperationException("Aggregator Execute failed");
}
