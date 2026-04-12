using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class PrefetchCountTests
{
    private readonly MessagingFixture _fixture;

    public PrefetchCountTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task PrefetchCount1_SlowHandler_ProcessesOneAtATime()
    {
        // Arrange
        const int messageCount = 3;
        var concurrencyLog = new ConcurrentBag<int>();
        var allDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueName = _fixture.GetUniqueQueueName("prefetch");

        // Shared state passed into each handler instance
        var sharedState = new SlowHandlerState(concurrencyLog, messageCount, allDone);

        var handlerReferences = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(SlowHandler),
                MessageType = typeof(TestMessage),
                RoutingKeys = new List<string>()
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerReferences);
        services.AddTransient<IMessageHandler<TestMessage>>(_ => new SlowHandler(sharedState));

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
                t.PrefetchCount = 1;
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        

        try
        {
            // Act: publish 3 messages
            for (int i = 0; i < messageCount; i++)
            {
                await bus.PublishAsync(new TestMessage(Guid.NewGuid()) { Content = $"msg-{i}" });
            }

            // Wait for all messages to be processed (up to 30 seconds)
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => allDone.TrySetCanceled());

            await allDone.Task;

            // Assert: at no point were more than 1 message being handled concurrently
            Assert.NotEmpty(concurrencyLog);
            Assert.All(concurrencyLog, c => Assert.Equal(1, c));
        }
        finally
        {
            await bus.DisposeAsync();
            (provider as IDisposable)?.Dispose();
        }
    }
}

file class SlowHandlerState
{
    public readonly ConcurrentBag<int> ConcurrencyLog;
    public readonly int MessageCount;
    public readonly TaskCompletionSource<bool> AllDone;
    public int CurrentConcurrency;
    public int ProcessedCount;

    public SlowHandlerState(ConcurrentBag<int> concurrencyLog, int messageCount, TaskCompletionSource<bool> allDone)
    {
        ConcurrencyLog = concurrencyLog;
        MessageCount = messageCount;
        AllDone = allDone;
    }
}

file class SlowHandler : IMessageHandler<TestMessage>
{
    private readonly SlowHandlerState _state;

    public IConsumeContext? Context { get; set; }

    public SlowHandler(SlowHandlerState state)
    {
        _state = state;
    }

    public async Task HandleAsync(TestMessage message)
    {
        var concurrency = Interlocked.Increment(ref _state.CurrentConcurrency);
        _state.ConcurrencyLog.Add(concurrency);

        await Task.Delay(500); // Simulate slow processing to test prefetch behavior

        Interlocked.Decrement(ref _state.CurrentConcurrency);

        var processed = Interlocked.Increment(ref _state.ProcessedCount);
        if (processed >= _state.MessageCount)
        {
            _state.AllDone.TrySetResult(true);
        }
    }
}
