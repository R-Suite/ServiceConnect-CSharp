using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(MessagingCollection))]
public class AggregatorTests
{
    private readonly MessagingFixture _fixture;

    public AggregatorTests(MessagingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Aggregator_BatchComplete_ExecutesWithAllMessages()
    {
        // Arrange
        var executed = new TaskCompletionSource<IList<TestMessage>>();
        var queueName = _fixture.GetUniqueQueueName("agg-batch");

        var handlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(BatchAggregator),
                MessageType = typeof(TestMessage),
                RoutingKeys = new List<string>()
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton(executed);
        services.AddTransient<Aggregator<TestMessage>, BatchAggregator>();

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
            builder.UseInMemoryPersistence();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            // Act: send 3 messages (batch size)
            for (var i = 0; i < 3; i++)
            {
                var msg = new TestMessage(Guid.NewGuid()) { Content = $"batch-{i}" };
                await bus.SendAsync(msg, new SendOptions { EndPoint = queueName });
            }

            // Wait for Execute to be called
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => executed.TrySetCanceled());
            var result = await executed.Task;

            // Assert
            Assert.Equal(3, result.Count);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Aggregator_Timeout_FlushesPartialBatch()
    {
        // Arrange
        var executed = new TaskCompletionSource<IList<TestMessage>>();
        var queueName = _fixture.GetUniqueQueueName("agg-timeout");

        var handlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(TimeoutAggregator),
                MessageType = typeof(TestMessage),
                RoutingKeys = new List<string>()
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IList<HandlerReference>>(handlerRefs);
        services.AddSingleton(executed);
        services.AddTransient<Aggregator<TestMessage>, TimeoutAggregator>();

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
            builder.UseInMemoryPersistence();
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();

        await bus.StartConsumingAsync();
        await Task.Delay(500);

        try
        {
            // Act: send only 2 messages (below batch size of 10)
            for (var i = 0; i < 2; i++)
            {
                var msg = new TestMessage(Guid.NewGuid()) { Content = $"timeout-{i}" };
                await bus.SendAsync(msg, new SendOptions { EndPoint = queueName });
            }

            // Wait for Execute to be called (should fire after ~2s timeout)
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            cts.Token.Register(() => executed.TrySetCanceled());
            var result = await executed.Task;

            // Assert
            Assert.Equal(2, result.Count);
        }
        finally
        {
            bus.Dispose();
            (provider as IDisposable)?.Dispose();
        }
    }
}

file class BatchAggregator : Aggregator<TestMessage>
{
    private readonly TaskCompletionSource<IList<TestMessage>> _tcs;
    public BatchAggregator(TaskCompletionSource<IList<TestMessage>> tcs) => _tcs = tcs;
    public override int BatchSize() => 3;
    public override TimeSpan Timeout() => TimeSpan.FromSeconds(30);
    public override void Execute(IList<TestMessage> messages) => _tcs.TrySetResult(messages);
}

file class TimeoutAggregator : Aggregator<TestMessage>
{
    private readonly TaskCompletionSource<IList<TestMessage>> _tcs;
    public TimeoutAggregator(TaskCompletionSource<IList<TestMessage>> tcs) => _tcs = tcs;
    public override int BatchSize() => 10;
    public override TimeSpan Timeout() => TimeSpan.FromSeconds(2);
    public override void Execute(IList<TestMessage> messages) => _tcs.TrySetResult(messages);
}
