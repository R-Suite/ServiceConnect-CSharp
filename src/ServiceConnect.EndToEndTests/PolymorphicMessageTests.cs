using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class PolymorphicMessageTests
{
    private readonly MessagingFixture _fixture;

    public PolymorphicMessageTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Publish_DerivedMessage_BaseHandlerReceivesIt()
    {
        // Arrange
        var tcs = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueName = _fixture.GetUniqueQueueName("polymorphic");

        var handlerReferences = new List<HandlerReference>
        {
            new HandlerReference
            {
                HandlerType = typeof(CallbackHandler<TestMessage>),
                MessageType = typeof(TestMessage),
                RoutingKeys = new List<string>()
            },
            // HandlerReference for DerivedTestMessage is needed so the bus subscribes
            // to this message type's exchange in RabbitMQ. Handler resolution happens
            // via DI + type hierarchy walking in the dispatcher.
            new HandlerReference
            {
                HandlerType = typeof(CallbackHandler<TestMessage>),
                MessageType = typeof(DerivedTestMessage),
                RoutingKeys = new List<string>()
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();

        // Register handler references before AddServiceConnect so TryAddSingleton keeps this list
        services.AddSingleton<IList<HandlerReference>>(handlerReferences);

        // Register the handler for the BASE type only
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
            // Act — publish a DERIVED message
            var correlationId = Guid.NewGuid();
            var sent = new DerivedTestMessage(correlationId)
            {
                Content = "polymorphic-test",
                Extra = "extra-data"
            };
            await bus.PublishAsync(sent);

            // Assert: wait up to 30 seconds for the handler to be called
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());

            var received = await tcs.Task;

            Assert.Equal("polymorphic-test", received.Content);
            Assert.Equal(correlationId, received.CorrelationId);
            Assert.IsType<DerivedTestMessage>(received);
            Assert.Equal("extra-data", ((DerivedTestMessage)received).Extra);
        }
        finally
        {
            await bus.DisposeAsync();
            if (provider is IAsyncDisposable asyncProvider) await asyncProvider.DisposeAsync();
        }
    }
}
