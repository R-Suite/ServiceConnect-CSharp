using Microsoft.Extensions.DependencyInjection;
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
public class ProcessManagerTests
{
    private readonly MessagingFixture _fixture;

    public ProcessManagerTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ProcessManager_TwoMessages_StateUpdatedCorrectly_InMemory()
    {
        // Arrange
        var secondHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueName = _fixture.GetUniqueQueueName("pm");
        var correlationId = Guid.NewGuid();

        var handlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(CounterProcessHandler),
                MessageType = typeof(TestMessage),
                RoutingKeys = new List<string>()
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton(secondHandled);
        services.AddTransient<IProcessHandler<TestProcessData, TestMessage>, CounterProcessHandler>();

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
            builder.UseInMemoryPersistence();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        

        try
        {
            // Act: send first message
            var msg1 = new TestMessage(correlationId) { Content = "first" };
            await bus.SendAsync(msg1, new SendOptions { EndPoint = queueName });

            // Act: send second message with same CorrelationId
            var msg2 = new TestMessage(correlationId) { Content = "second" };
            await bus.SendAsync(msg2, new SendOptions { EndPoint = queueName });

            // Wait for second handler invocation
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => secondHandled.TrySetCanceled());
            await secondHandled.Task;

            // Allow time for the ProcessManagerProcessor to persist after handler completes
            await Task.Delay(500);

            // Assert: verify persisted state
            var finder = provider.GetRequiredService<IProcessManagerFinder>();
            var mapper = new TestProcessManagerPropertyMapper();
            mapper.ConfigureMapping<TestProcessData, TestMessage>(d => d.CorrelationId, m => m.CorrelationId);
            var result = finder.FindData<TestProcessData>(mapper, new TestMessage(correlationId));

            Assert.NotNull(result);
            Assert.Equal(2, result.Data.Counter);
            Assert.Equal("second", result.Data.LastContent);
        }
        finally
        {
            await bus.DisposeAsync();
            if (provider is IAsyncDisposable asyncProvider) await asyncProvider.DisposeAsync();
        }
    }
}

file class CounterProcessHandler : IProcessHandler<TestProcessData, TestMessage>
{
    private readonly TaskCompletionSource<bool> _secondHandled;

    public CounterProcessHandler(TaskCompletionSource<bool> secondHandled) =>
        _secondHandled = secondHandled;

    public IConsumeContext? Context { get; set; }

    public Task HandleAsync(TestMessage message, TestProcessData data)
    {
        data.Counter++;
        data.LastContent = message.Content;
        if (data.Counter >= 2) _secondHandled.TrySetResult(true);
        return Task.CompletedTask;
    }
}
