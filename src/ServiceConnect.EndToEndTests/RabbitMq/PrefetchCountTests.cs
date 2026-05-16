using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.DependencyInjection;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class PrefetchCountTests(MessagingFixture fixture)
{
    private readonly MessagingFixture _fixture = fixture;

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
                MessageType = typeof(TestMessage)
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IReadOnlyList<HandlerReference>>(handlerReferences);
        services.AddTransient<IMessageHandler<TestMessage>>(_ => new SlowHandler(sharedState));

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
                t.PrefetchCount = 1;
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
            if (provider is IAsyncDisposable asyncProvider)
            {
                await asyncProvider.DisposeAsync();
            }
        }
    }
}

file class SlowHandlerState(ConcurrentBag<int> concurrencyLog, int messageCount, TaskCompletionSource<bool> allDone)
{
    public readonly ConcurrentBag<int> ConcurrencyLog = concurrencyLog;
    public readonly int MessageCount = messageCount;
    public readonly TaskCompletionSource<bool> AllDone = allDone;
    public int CurrentConcurrency;
    public int ProcessedCount;
}

file class SlowHandler(SlowHandlerState state) : IMessageHandler<TestMessage>
{
    private readonly SlowHandlerState _state = state;

    public async Task HandleAsync(TestMessage message, IConsumeContext context, CancellationToken cancellationToken = default)
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
