using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class ConsumerCountE2ETests
{
    private readonly MessagingFixture _fixture;

    public ConsumerCountE2ETests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ConsumerCount2_Sends10Messages_AllProcessed()
    {
        // Arrange
        const int messageCount = 10;
        var threadIds = new ConcurrentBag<int>();
        var countdown = new CountdownEvent(messageCount);
        var queueName = _fixture.GetUniqueQueueName("consumer-count");

        var handlerState = new ConsumerCountHandlerState(threadIds, countdown);

        var handlerReferences = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(ConsumerCountHandler),
                MessageType = typeof(TestMessage)
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerReferences);
        services.AddSingleton(handlerState);
        services.AddTransient<IMessageHandler<TestMessage>, ConsumerCountHandler>();

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
            builder.ConfigureBus(b =>
            {
                b.ScanForMessageHandlers = false;
                b.ConsumerCount = 2;
            });
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        

        try
        {
            // Act: publish 10 messages
            for (int i = 0; i < messageCount; i++)
            {
                await bus.PublishAsync(new TestMessage(Guid.NewGuid()) { Content = $"msg-{i}" });
            }

            // Assert: all 10 messages processed within 30 seconds
            var completed = countdown.Wait(TimeSpan.FromSeconds(30));

            Assert.True(completed, $"Only {messageCount - countdown.CurrentCount} of {messageCount} messages were processed within the timeout.");
            Assert.Equal(messageCount, threadIds.Count);
        }
        finally
        {
            await bus.DisposeAsync();
            if (provider is IAsyncDisposable asyncProvider) await asyncProvider.DisposeAsync();
            countdown.Dispose();
        }
    }
}

file class ConsumerCountHandlerState
{
    public readonly ConcurrentBag<int> ThreadIds;
    public readonly CountdownEvent Countdown;

    public ConsumerCountHandlerState(ConcurrentBag<int> threadIds, CountdownEvent countdown)
    {
        ThreadIds = threadIds;
        Countdown = countdown;
    }
}

file class ConsumerCountHandler : IMessageHandler<TestMessage>
{
    private readonly ConsumerCountHandlerState _state;

    public IConsumeContext Context { get; set; } = null!;

    public ConsumerCountHandler(ConsumerCountHandlerState state)
    {
        _state = state;
    }

    public Task HandleAsync(TestMessage message, CancellationToken cancellationToken = default)
    {
        _state.ThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
        _state.Countdown.Signal();
        return Task.CompletedTask;
    }
}
