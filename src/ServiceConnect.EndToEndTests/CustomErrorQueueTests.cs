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
public class CustomErrorQueueTests
{
    private readonly MessagingFixture _fixture;

    public CustomErrorQueueTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

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
                MessageType = typeof(TestMessage),
                RoutingKeys = new List<string>()
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerReferences);
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
                t.ClientSettings["Port"] = _fixture.RabbitMqPort;
                t.ClientSettings["RetryCount"] = 3;
                t.ClientSettings["RetrySeconds"] = 1;
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
        await Task.Delay(500);

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
            using var conn = factory.CreateConnection();
            using var channel = conn.CreateModel();

            BasicGetResult? errorMsg = null;
            for (int i = 0; i < 30 && errorMsg == null; i++)
            {
                errorMsg = channel.BasicGet(customErrorQueueName, autoAck: true);
                if (errorMsg == null) await Task.Delay(1000);
            }

            Assert.NotNull(errorMsg);

            // Verify it's our message
            var headers = errorMsg.BasicProperties.Headers;
            Assert.True(headers.ContainsKey("Exception"));
            var exceptionJson = Encoding.UTF8.GetString((byte[])headers["Exception"]);
            Assert.Contains("Always fails", exceptionJson);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}
