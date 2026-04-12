using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Helpers;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class ProcessManagerExceptionTests
{
    private readonly MessagingFixture _fixture;

    public ProcessManagerExceptionTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ProcessManagerHandler_Throws_MessageSentToErrorQueue()
    {
        // Arrange
        var queueName = _fixture.GetUniqueQueueName("pm-exception");
        var errorQueueName = _fixture.GetUniqueQueueName("pm-exception-eq");
        var correlationId = Guid.NewGuid();
        const int maxRetries = 1;
        const int retryDelay = 1000;

        var mapper = new TestProcessManagerPropertyMapper();
        mapper.ConfigureMapping<TestProcessData, TestMessage>(d => d.CorrelationId, m => m.CorrelationId);

        var handlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(ThrowingProcessHandler),
                MessageType = typeof(TestMessage),
                RoutingKeys = new List<string>()
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton<IProcessManagerPropertyMapper>(mapper);
        services.AddTransient<IProcessHandler<TestProcessData, TestMessage>, ThrowingProcessHandler>();

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
            // Act
            var msg = new TestMessage(correlationId) { Content = "throw-me" };
            await bus.SendAsync(msg, new SendOptions { EndPoint = queueName });

            // Poll error queue with raw RabbitMQ — wait long enough for retries
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

            // Assert
            Assert.NotNull(errorMsg);

            var headers = errorMsg.BasicProperties.Headers!;
            Assert.True(headers.ContainsKey("Exception"));
            var exceptionJson = Encoding.UTF8.GetString((byte[])headers["Exception"]!);
            Assert.Contains("PM handler exploded", exceptionJson);
        }
        finally
        {
            await bus.DisposeAsync();
            (provider as IDisposable)?.Dispose();
        }
    }
}

file class ThrowingProcessHandler : IProcessHandler<TestProcessData, TestMessage>
{
    public IConsumeContext? Context { get; set; }

    public Task HandleAsync(TestMessage message, TestProcessData data)
    {
        throw new InvalidOperationException("PM handler exploded");
    }
}
