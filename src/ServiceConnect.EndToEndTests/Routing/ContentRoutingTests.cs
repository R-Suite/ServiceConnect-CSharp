using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class ContentRoutingTests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Publish_DifferentMessageTypes_RoutedToCorrectHandlers()
    {
        // Arrange
        var testMessageTcs = new TaskCompletionSource<TestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stepMessageTcs = new TaskCompletionSource<StepMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueName = _fixture.GetUniqueQueueName("content-routing");

        var handlerReferences = new List<HandlerReference>
        {
            new() { HandlerType = typeof(CallbackHandler<TestMessage>), MessageType = typeof(TestMessage) },
            new() { HandlerType = typeof(CallbackHandler<StepMessage>), MessageType = typeof(StepMessage) }
        };

        var services = new ServiceCollection();
        services.AddLogging();

        // Register handler references before AddServiceConnect so TryAddSingleton keeps this list
        services.AddSingleton<IList<HandlerReference>>(handlerReferences);

        // Register each handler backed by its own callback
        services.AddTransient<IMessageHandler<TestMessage>>(_ =>
            new CallbackHandler<TestMessage>(msg => testMessageTcs.TrySetResult(msg)));
        services.AddTransient<IMessageHandler<StepMessage>>(_ =>
            new CallbackHandler<StepMessage>(msg => stepMessageTcs.TrySetResult(msg)));

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

        await bus.StartConsumingAsync();


        try
        {
            // Act: publish one of each message type
            var testCorrelationId = Guid.NewGuid();
            var stepCorrelationId = Guid.NewGuid();

            var testMsg = new TestMessage(testCorrelationId) { Content = "routed-test-message" };
            var stepMsg = new StepMessage(stepCorrelationId) { CurrentStep = "RouteStep1" };

            await bus.PublishAsync(testMsg);
            await bus.PublishAsync(stepMsg);

            // Assert: each handler receives exactly its own message type
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() =>
            {
                testMessageTcs.TrySetCanceled();
                stepMessageTcs.TrySetCanceled();
            });

            var receivedTest = await testMessageTcs.Task;
            var receivedStep = await stepMessageTcs.Task;

            Assert.Equal("routed-test-message", receivedTest.Content);
            Assert.Equal(testCorrelationId, receivedTest.CorrelationId);

            Assert.Equal("RouteStep1", receivedStep.CurrentStep);
            Assert.Equal(stepCorrelationId, receivedStep.CorrelationId);
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
