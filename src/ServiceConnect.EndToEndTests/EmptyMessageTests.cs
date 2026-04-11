using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class EmptyMessageTests
{
    private readonly MessagingFixture _fixture;

    public EmptyMessageTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Publish_EmptyContent_HandlerReceivesMessageWithDefaults()
    {
        // Arrange
        var tcs = new TaskCompletionSource<TestMessage>();
        var queueName = _fixture.GetUniqueQueueName("emptymsg");

        var handlerReferences = new List<HandlerReference>
        {
            new HandlerReference
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
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            // Act — publish a TestMessage with no Content set (defaults to string.Empty)
            var correlationId = Guid.NewGuid();
            var sent = new TestMessage(correlationId); // Content not set
            await bus.PublishAsync(sent);

            // Assert
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());

            var received = await tcs.Task;

            Assert.Equal(correlationId, received.CorrelationId);
            Assert.Equal(string.Empty, received.Content);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}
