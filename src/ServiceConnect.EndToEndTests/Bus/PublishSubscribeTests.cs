using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

public class CallbackHandler<T>(Action<T> callback) : IMessageHandler<T> where T : Message
{
    private readonly Action<T> _callback = callback;

    public Task HandleAsync(T message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        _callback(message);
        return Task.CompletedTask;
    }
}

[Collection(nameof(MessagingCollection))]
public class PublishSubscribeTests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Publish_SubscriberReceivesMessage()
    {
        // Arrange
        var tcs = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueName = _fixture.GetUniqueQueueName("pubsub");

        var handlerReferences = new List<HandlerReference>
        {
            new() {
                HandlerType = typeof(CallbackHandler<TestMessage>),
                MessageType = typeof(TestMessage)
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();

        // Register handler references before AddServiceConnect so TryAddSingleton keeps this list
        services.AddSingleton<IList<HandlerReference>>(handlerReferences);

        // Register the handler, backed by our callback
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
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        // Start consuming
        await bus.StartConsumingAsync();


        try
        {
            // Act
            var correlationId = Guid.NewGuid();
            var sent = new TestMessage(correlationId) { Content = "hello publish-subscribe" };
            await bus.PublishAsync(sent);

            // Assert: wait up to 30 seconds for the handler to be called
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());

            var received = await tcs.Task;

            Assert.Equal("hello publish-subscribe", received.Content);
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
