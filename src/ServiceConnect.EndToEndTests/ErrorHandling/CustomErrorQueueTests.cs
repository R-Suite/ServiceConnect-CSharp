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
public class CustomErrorQueueTests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task CustomErrorQueueName_FailedMessage_SentToCustomQueue()
    {
        // Arrange
        var queueName = _fixture.GetUniqueQueueName("custom-error");
        var customErrorQueueName = _fixture.GetUniqueQueueName("my-custom-errors");
        const int maxRetries = 1;
        const int retryDelay = 1000;

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
        services.AddSingleton<IReadOnlyList<HandlerReference>>(handlerReferences);
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(_ =>
                throw new InvalidOperationException("Always fails")));

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.MaxRetries = maxRetries;
                t.RetryDelay = retryDelay;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3);
                t.SetClientSetting("RetrySeconds", 1);
                t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
            });
            builder.ConfigureQueues(q =>
            {
                q.QueueName = queueName;
                q.ErrorQueueName = customErrorQueueName;
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
            var sent = new TestMessage(correlationId) { Content = "custom-error-queue" };
            await bus.PublishAsync(sent);

            // Assert: message appears in the custom-named error queue
            var factory = new ConnectionFactory
            {
                HostName = _fixture.RabbitMqHostname,
                Port = _fixture.RabbitMqPort,
                UserName = _fixture.RabbitMqUsername,
                Password = _fixture.RabbitMqPassword
            };
            await using var conn = await factory.CreateConnectionAsync();
            await using var channel = await conn.CreateChannelAsync();

            var errorMsg = await TestPolling.WaitForAsync(
                async () => await channel.BasicGetAsync(customErrorQueueName, autoAck: true),
                timeout: TimeSpan.FromSeconds(30));

            Assert.NotNull(errorMsg);

            // Verify it's our message
            var headers = errorMsg.BasicProperties.Headers!;
            Assert.True(headers.ContainsKey("Exception"));
            var exceptionJson = Encoding.UTF8.GetString((byte[])headers["Exception"]!);
            Assert.Contains("Always fails", exceptionJson);
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
